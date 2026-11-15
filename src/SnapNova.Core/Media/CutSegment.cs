namespace SnapNova.Core.Media;

/// <summary>A part of a video in seconds (start inclusive, end exclusive).</summary>
public readonly record struct CutSegment(double Start, double End)
{
    public double Length => Math.Max(0, End - Start);
}

/// <summary>
/// Cut of the video editor: a trimmed range (start/end handles) and parts cut out of it (e.g. the middle).
/// The result is the trimmed range minus the cut-out parts.
/// </summary>
public sealed class TrimEdit
{
    private const double MinLength = 0.1;
    private readonly List<CutSegment> _removed = [];

    public TrimEdit(double duration) => Reset(duration);

    public double Duration { get; private set; }

    /// <summary>Start and end of the result (trim handles).</summary>
    public double Start { get; private set; }

    public double End { get; private set; }

    /// <summary>Parts cut out, sorted, never overlapping, always inside Start…End.</summary>
    public IReadOnlyList<CutSegment> Removed => _removed;

    /// <summary>Marked range that "cut out" would remove (null = nothing marked).</summary>
    public CutSegment? Selection { get; private set; }

    public void Reset(double duration)
    {
        Duration = Math.Max(MinLength, duration);
        Start = 0;
        End = Duration;
        _removed.Clear();
        Selection = null;
    }

    public bool IsUncut => Start <= 0.001 && End >= Duration - 0.001 && _removed.Count == 0;

    /// <summary>The parts that remain, in order.</summary>
    public IReadOnlyList<CutSegment> Kept
    {
        get
        {
            var kept = new List<CutSegment>();
            var from = Start;
            foreach (var r in _removed)
            {
                if (r.Start - from >= 0.01)
                {
                    kept.Add(new CutSegment(from, r.Start));
                }

                from = Math.Max(from, r.End);
            }

            if (End - from >= 0.01)
            {
                kept.Add(new CutSegment(from, End));
            }

            return kept;
        }
    }

    public double KeptLength => Kept.Sum(k => k.Length);

    public bool IsKept(double time) => time >= Start && time < End && !_removed.Any(r => time >= r.Start && time < r.End);

    /// <summary>Playback: the next kept time at or after <paramref name="time"/>, or null after the end.</summary>
    public double? NextKept(double time)
    {
        foreach (var k in Kept)
        {
            if (k.End > time + 0.01)
            {
                return Math.Max(k.Start, time);
            }
        }

        return null;
    }

    public double FirstKept => Kept.Count > 0 ? Kept[0].Start : 0;

    // ---------------------------------------------------------------- trim handles

    /// <summary>Moves the start handle (cut-out parts before it are dropped or shortened).</summary>
    public double SetStart(double time)
    {
        Start = Math.Clamp(time, 0, End - MinLength);
        Normalize();
        return Start;
    }

    public double SetEnd(double time)
    {
        End = Math.Clamp(time, Start + MinLength, Duration);
        Normalize();
        return End;
    }

    // ---------------------------------------------------------------- cut out

    public void SetSelection(double a, double b)
    {
        var from = Math.Clamp(Math.Min(a, b), 0, Duration);
        var to = Math.Clamp(Math.Max(a, b), 0, Duration);
        Selection = to - from >= 0.05 ? new CutSegment(from, to) : null;
    }

    public void ClearSelection() => Selection = null;

    /// <summary>Cuts the marked range out (joined with touching cut-out parts). Returns its index or -1.</summary>
    public int CutSelection()
    {
        if (Selection is not { } s)
        {
            return -1;
        }

        Selection = null;
        var from = Math.Max(s.Start, Start);
        var to = Math.Min(s.End, End);
        if (to - from < 0.05)
        {
            return -1;
        }

        _removed.Add(new CutSegment(from, to));
        Normalize();
        return IndexAt(from + (to - from) / 2);
    }

    /// <summary>Undoes one cut-out part (the content comes back).</summary>
    public void Restore(int index)
    {
        if (index >= 0 && index < _removed.Count)
        {
            _removed.RemoveAt(index);
        }
    }

    /// <summary>Index of the cut-out part under <paramref name="time"/>, or -1.</summary>
    public int IndexAt(double time)
    {
        for (var i = 0; i < _removed.Count; i++)
        {
            if (time >= _removed[i].Start && time <= _removed[i].End)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Drags one edge of a cut-out part; it stays inside its neighbours and at least 0.1 s long.</summary>
    public double MoveEdge(int index, bool startEdge, double time)
    {
        if (index < 0 || index >= _removed.Count)
        {
            return time;
        }

        var r = _removed[index];
        var lower = index > 0 ? _removed[index - 1].End : Start;
        var upper = index < _removed.Count - 1 ? _removed[index + 1].Start : End;
        if (startEdge)
        {
            time = Math.Clamp(time, lower, r.End - MinLength);
            _removed[index] = r with { Start = time };
        }
        else
        {
            time = Math.Clamp(time, r.Start + MinLength, upper);
            _removed[index] = r with { End = time };
        }

        return time;
    }

    /// <summary>Sorts, clips to Start…End and joins overlapping cut-out parts.</summary>
    private void Normalize()
    {
        var parts = _removed
            .Select(r => new CutSegment(Math.Max(r.Start, Start), Math.Min(r.End, End)))
            .Where(r => r.Length >= 0.05)
            .OrderBy(r => r.Start)
            .ToList();
        _removed.Clear();
        foreach (var r in parts)
        {
            if (_removed.Count > 0 && r.Start <= _removed[^1].End + 0.001)
            {
                _removed[^1] = _removed[^1] with { End = Math.Max(_removed[^1].End, r.End) };
            }
            else
            {
                _removed.Add(r);
            }
        }
    }
}
