namespace SnapNova.Core.Capture;

/// <summary>
/// Joins the frames of a scrolling capture into one tall image.
/// Each frame has the same size. Rows that do not change between two frames at the top and bottom (sticky headers,
/// footers, cookie banners) are treated as fixed and appear only once. The scroll offset is the shift with the best
/// match of row hashes inside the scrolling part.
/// </summary>
public sealed class ScrollStitcher(int maxHeight = 30_000)
{
    private readonly List<byte[]> _rows = [];
    private PixelImage? _last;
    private ulong[]? _lastHashes;
    private int _header = -1;
    private int _footer;
    private byte[][]? _footerRows;

    public int Width => _last?.Width ?? 0;

    public int Height => _rows.Count + (_footerRows?.Length ?? 0);

    public bool IsFull => Height >= maxHeight;

    /// <summary>Number of frames in a row that added nothing (the end of the page is reached).</summary>
    public int UnchangedFrames { get; private set; }

    /// <summary>Adds a frame. Returns the number of new rows (0 = no movement or no reliable match).</summary>
    public int Add(PixelImage frame)
    {
        var hashes = RowHashes(frame);
        if (_last is null || _lastHashes is null)
        {
            _last = frame;
            _lastHashes = hashes;
            for (var y = 0; y < frame.Height; y++)
            {
                _rows.Add(Row(frame, y));
            }

            return frame.Height;
        }

        if (frame.Width != _last.Width || frame.Height != _last.Height)
        {
            throw new ArgumentException("All frames of a scrolling capture must have the same size.");
        }

        var h = frame.Height;
        var top = 0;
        while (top < h / 3 && hashes[top] == _lastHashes[top])
        {
            top++;
        }

        var bottom = 0;
        while (bottom < h / 3 && hashes[h - 1 - bottom] == _lastHashes[h - 1 - bottom])
        {
            bottom++;
        }

        if (top + bottom >= h - 8)
        {
            // Identical frame: nothing scrolled.
            UnchangedFrames++;
            return 0;
        }

        // The first real movement decides the fixed header and footer.
        if (_header < 0)
        {
            _header = top;
            _footer = bottom;

            // The footer of the first frame was already added as content: remove it, it goes at the very end.
            _footerRows = Enumerable.Range(h - _footer, _footer).Select(y => _rows[y]).ToArray();
            _rows.RemoveRange(h - _footer, _footer);
        }

        var shift = FindShift(_lastHashes, hashes, _header, h - _footer);
        if (shift <= 0)
        {
            UnchangedFrames++;
            return 0;
        }

        UnchangedFrames = 0;
        var added = 0;
        for (var y = h - _footer - shift; y < h - _footer && !IsFull; y++)
        {
            _rows.Add(Row(frame, y));
            added++;
        }

        _footerRows = Enumerable.Range(h - _footer, _footer).Select(y => Row(frame, y)).ToArray();
        _last = frame;
        _lastHashes = hashes;
        return added;
    }

    public PixelImage Result()
    {
        if (_last is null)
        {
            throw new InvalidOperationException("No frames.");
        }

        var all = _rows.Concat(_footerRows ?? []).ToList();
        var stride = Width * 4;
        var pixels = new byte[stride * all.Count];
        for (var y = 0; y < all.Count; y++)
        {
            Buffer.BlockCopy(all[y], 0, pixels, y * stride, stride);
        }

        return new PixelImage(Width, all.Count, pixels, stride);
    }

    /// <summary>Shift s so that previous[y + s] == current[y] in the scrolling band; best match ratio wins (≥ 85 %).</summary>
    internal static int FindShift(ulong[] previous, ulong[] current, int start, int end)
    {
        var band = end - start;
        var best = 0;
        var bestScore = 0.0;
        for (var s = 1; s < band - 8; s++)
        {
            var compared = band - s;
            var matches = 0;
            var distinct = 0;
            for (var y = start; y < end - s; y++)
            {
                if (previous[y + s] == current[y])
                {
                    matches++;
                    if (y > start && current[y] != current[y - 1])
                    {
                        distinct++;
                    }
                }
            }

            // Uniform areas (empty background) match at every shift – require some structure.
            var score = (double)matches / compared;
            if (score > bestScore && distinct >= Math.Min(8, compared / 20))
            {
                bestScore = score;
                best = s;
            }
        }

        return bestScore >= 0.85 ? best : 0;
    }

    private static ulong[] RowHashes(PixelImage image)
    {
        var hashes = new ulong[image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            // FNV-1a over the row (every pixel; ignoring alpha).
            var hash = 14695981039346656037UL;
            var offset = y * image.Stride;
            for (var x = 0; x < image.Width * 4; x++)
            {
                if ((x & 3) == 3)
                {
                    continue;
                }

                hash = (hash ^ image.Pixels[offset + x]) * 1099511628211UL;
            }

            hashes[y] = hash;
        }

        return hashes;
    }

    private static byte[] Row(PixelImage image, int y)
    {
        var row = new byte[image.Width * 4];
        Buffer.BlockCopy(image.Pixels, y * image.Stride, row, 0, row.Length);
        return row;
    }
}
