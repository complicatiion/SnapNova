using System.Text.Json.Serialization;

namespace SnapNova.Core.Capture;

/// <summary>Integer rectangle in physical pixels (screen or image coordinates).</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    [JsonIgnore]
    public int Right => X + Width;

    [JsonIgnore]
    public int Bottom => Y + Height;

    [JsonIgnore]
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public ScreenRect Intersect(ScreenRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? new ScreenRect(left, top, right - left, bottom - top) : default;
    }

    public ScreenRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>Rectangle from two corner points (any drag direction).</summary>
    public static ScreenRect FromPoints(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    /// <summary>Hardware video encoders need even sizes: shrinks width/height by one pixel when odd.</summary>
    public ScreenRect EvenSize() => this with { Width = Width & ~1, Height = Height & ~1 };

    public override string ToString() => $"{X},{Y} {Width}×{Height}";
}

/// <summary>32-bit BGRA pixels (premultiplied alpha = opaque screen content), top-down rows.</summary>
public sealed class PixelImage
{
    public PixelImage(int width, int height, byte[] pixels, int stride)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image must not be empty.");
        }

        if (stride < width * 4 || pixels.Length < stride * height)
        {
            throw new ArgumentException("Pixel buffer too small.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
        Stride = stride;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public byte[] Pixels { get; }

    /// <summary>Copy of a part of the image (clamped to the image bounds).</summary>
    public PixelImage Crop(ScreenRect rect)
    {
        var r = rect.Intersect(new ScreenRect(0, 0, Width, Height));
        if (r.IsEmpty)
        {
            throw new ArgumentException("Crop rectangle is outside the image.", nameof(rect));
        }

        var stride = r.Width * 4;
        var pixels = new byte[stride * r.Height];
        for (var y = 0; y < r.Height; y++)
        {
            Buffer.BlockCopy(Pixels, (r.Y + y) * Stride + r.X * 4, pixels, y * stride, stride);
        }

        return new PixelImage(r.Width, r.Height, pixels, stride);
    }
}

/// <summary>One monitor of a frozen screen: bounds in virtual-desktop pixels and its pixels.</summary>
public sealed record MonitorShot(string DeviceName, ScreenRect Bounds, bool IsPrimary, PixelImage Image);

/// <summary>A top-level window at the moment of the freeze (z-order: first = topmost).</summary>
public sealed record WindowInfo(nint Handle, ScreenRect Bounds, string Title, string ProcessName);

/// <summary>Everything captured at the freeze: all monitors plus the window list for snapping.</summary>
public sealed record FrozenScreen(IReadOnlyList<MonitorShot> Monitors, IReadOnlyList<WindowInfo> Windows, DateTimeOffset Taken)
{
    /// <summary>Topmost window under a virtual-desktop point.</summary>
    public WindowInfo? WindowAt(int x, int y) => Windows.FirstOrDefault(w => w.Bounds.Contains(x, y));

    public MonitorShot? MonitorAt(int x, int y) =>
        Monitors.FirstOrDefault(m => m.Bounds.Contains(x, y)) ?? Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();
}

/// <summary>Origin of a capture, stored in the .snova document and shown in the gallery.</summary>
public sealed record CaptureInfo(DateTimeOffset Created, string? SourceApp, string? WindowTitle, ScreenRect ScreenArea);
