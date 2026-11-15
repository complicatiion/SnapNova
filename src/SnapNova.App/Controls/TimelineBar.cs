using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SnapNova.Core.Media;

namespace SnapNova.App.Controls;

/// <summary>
/// Timeline of the video editor.
/// <list type="bullet">
/// <item>Orange brackets: start and end of the result – drag them to trim. Outside is dimmed.</item>
/// <item>Drag on the bar: moves the playhead. Shift + drag (or the lower half of the bar): marks a range to cut out.</item>
/// <item>Cut-out parts are hatched red; click selects one, its edges can be dragged.</item>
/// </list>
/// </summary>
public sealed class TimelineBar : Control
{
    private static readonly IBrush Track = new SolidColorBrush(Color.Parse("#2A2A2A"));
    private static readonly IBrush KeepFill = new SolidColorBrush(Color.Parse("#55FF8C00"));
    private static readonly IBrush TrimmedFill = new SolidColorBrush(Color.Parse("#C0101010"));
    private static readonly IBrush RemoveFill = new SolidColorBrush(Color.Parse("#50E53935"));
    private static readonly IPen RemoveHatch = new Pen(new SolidColorBrush(Color.Parse("#B0E53935")), 1.5);
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#FF8C00"));
    private static readonly IPen AccentPen = new Pen(new SolidColorBrush(Color.Parse("#FF8C00")), 2);
    private static readonly IPen SelectedPen = new Pen(Brushes.White, 2);
    private static readonly IBrush SelectionFill = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
    private static readonly IPen SelectionPen = new Pen(Brushes.White, 1.5, new DashStyle([4, 3], 0));
    private static readonly IPen HeadPen = new Pen(Brushes.White, 2);
    private const double Grab = 7;

    private enum Drag
    {
        None,
        Seek,
        TrimStart,
        TrimEnd,
        EdgeStart,
        EdgeEnd,
        Select
    }

    private Drag _drag;
    private int _edgeIndex;
    private double _selectFrom;

    public TimelineBar()
    {
        Height = 52;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public TrimEdit? Edit { get; set; }

    public double Position { get; set; }

    /// <summary>Selected cut-out part (-1 = none).</summary>
    public int SelectedRemoved { get; set; } = -1;

    /// <summary>Seek to a time (seconds); <c>final</c> = mouse released.</summary>
    public event Action<double, bool>? SeekRequested;

    /// <summary>The cut changed (handles, edges, marked range) – redraw and update texts.</summary>
    public event Action? EditChanged;

    public event Action<int>? RemovedSelected;

    private Rect BarRect => new(0, 10, Bounds.Width, Bounds.Height - 20);

    private double TimeAt(double x) => Edit is null || Bounds.Width <= 0 ? 0 : Math.Clamp(x / Bounds.Width, 0, 1) * Edit.Duration;

    private double XAt(double time) => Edit is null ? 0 : time / Edit.Duration * Bounds.Width;

    public override void Render(DrawingContext context)
    {
        var bar = BarRect;
        context.DrawRectangle(Track, null, new RoundedRect(bar, 6));
        if (Edit is null)
        {
            return;
        }

        // Kept range, then the trimmed-away outside (dimmed).
        var start = XAt(Edit.Start);
        var end = XAt(Edit.End);
        context.DrawRectangle(KeepFill, null, new Rect(start, bar.Top, Math.Max(1, end - start), bar.Height));
        context.DrawRectangle(TrimmedFill, null, new Rect(0, bar.Top, start, bar.Height));
        context.DrawRectangle(TrimmedFill, null, new Rect(end, bar.Top, Math.Max(0, bar.Right - end), bar.Height));

        // Cut-out parts: hatched red, the selected one framed.
        for (var i = 0; i < Edit.Removed.Count; i++)
        {
            var r = Edit.Removed[i];
            var rect = new Rect(XAt(r.Start), bar.Top, Math.Max(2, XAt(r.End) - XAt(r.Start)), bar.Height);
            context.DrawRectangle(RemoveFill, null, rect);
            using (context.PushClip(rect))
            {
                for (var x = rect.Left - rect.Height; x < rect.Right; x += 7)
                {
                    context.DrawLine(RemoveHatch, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
                }
            }

            if (i == SelectedRemoved)
            {
                context.DrawRectangle(null, SelectedPen, rect.Deflate(1));
                DrawGrip(context, rect.Left, bar, Brushes.White);
                DrawGrip(context, rect.Right, bar, Brushes.White);
            }
        }

        // Marked range (not cut yet).
        if (Edit.Selection is { } s)
        {
            var rect = new Rect(XAt(s.Start), bar.Top - 3, Math.Max(2, XAt(s.End) - XAt(s.Start)), bar.Height + 6);
            context.DrawRectangle(SelectionFill, SelectionPen, rect);
        }

        // Trim brackets.
        DrawBracket(context, start, bar, left: true);
        DrawBracket(context, end, bar, left: false);

        // Playhead with knob.
        var hx = XAt(Position);
        context.DrawLine(HeadPen, new Point(hx, 2), new Point(hx, Bounds.Height - 2));
        context.DrawEllipse(Brushes.White, null, new Point(hx, 5), 4, 4);
    }

    private static void DrawBracket(DrawingContext context, double x, Rect bar, bool left)
    {
        var w = 7;
        var rect = new Rect(left ? x : x - w, bar.Top - 4, w, bar.Height + 8);
        context.DrawRectangle(Accent, null, new RoundedRect(rect, left ? 4 : 0, left ? 0 : 4, left ? 0 : 4, left ? 4 : 0));
        var grip = left ? x + 3.5 : x - 3.5;
        context.DrawLine(new Pen(Brushes.White, 1), new Point(grip, bar.Top + bar.Height / 2 - 5), new Point(grip, bar.Top + bar.Height / 2 + 5));
    }

    private static void DrawGrip(DrawingContext context, double x, Rect bar, IBrush brush) =>
        context.DrawRectangle(brush, null, new RoundedRect(new Rect(x - 3, bar.Bottom + 1, 6, 7), 2));

    // ---------------------------------------------------------------- input

    private (Drag Kind, int Index) HitTest(Point p)
    {
        if (Edit is null)
        {
            return (Drag.None, -1);
        }

        if (Math.Abs(p.X - XAt(Edit.Start)) <= Grab)
        {
            return (Drag.TrimStart, -1);
        }

        if (Math.Abs(p.X - XAt(Edit.End)) <= Grab)
        {
            return (Drag.TrimEnd, -1);
        }

        for (var i = 0; i < Edit.Removed.Count; i++)
        {
            if (Math.Abs(p.X - XAt(Edit.Removed[i].Start)) <= Grab)
            {
                return (Drag.EdgeStart, i);
            }

            if (Math.Abs(p.X - XAt(Edit.Removed[i].End)) <= Grab)
            {
                return (Drag.EdgeEnd, i);
            }
        }

        return (Drag.None, Edit.IndexAt(TimeAt(p.X)));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Edit is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        var p = point.Position;
        var time = TimeAt(p.X);
        var (kind, index) = HitTest(p);

        // Right click: select what is under the mouse and put the playhead there – the menu acts on it.
        if (point.Properties.IsRightButtonPressed)
        {
            RemovedSelected?.Invoke(kind is Drag.EdgeStart or Drag.EdgeEnd ? index : Edit.IndexAt(time));
            SeekRequested?.Invoke(time, true);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Pointer.Capture(this);
        if (kind is Drag.TrimStart or Drag.TrimEnd or Drag.EdgeStart or Drag.EdgeEnd)
        {
            _drag = kind;
            _edgeIndex = index;
            if (kind is Drag.EdgeStart or Drag.EdgeEnd)
            {
                RemovedSelected?.Invoke(index);
            }

            return;
        }

        // Shift or the lower half of the bar: mark a range to cut out.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || p.Y > BarRect.Top + BarRect.Height * 0.6)
        {
            _drag = Drag.Select;
            _selectFrom = time;
            Edit.ClearSelection();
            RemovedSelected?.Invoke(-1);
            EditChanged?.Invoke();
            return;
        }

        _drag = Drag.Seek;
        RemovedSelected?.Invoke(index);
        SeekRequested?.Invoke(time, false);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Edit is null)
        {
            return;
        }

        var p = e.GetPosition(this);
        var time = TimeAt(p.X);
        switch (_drag)
        {
            case Drag.None:
                var (kind, _) = HitTest(p);
                Cursor = new Cursor(kind != Drag.None ? StandardCursorType.SizeWestEast
                    : p.Y > BarRect.Top + BarRect.Height * 0.6 ? StandardCursorType.Cross
                    : StandardCursorType.Hand);
                return;
            case Drag.Seek:
                SeekRequested?.Invoke(time, false);
                return;
            case Drag.TrimStart:
                SeekRequested?.Invoke(Edit.SetStart(time), false);
                break;
            case Drag.TrimEnd:
                SeekRequested?.Invoke(Edit.SetEnd(time), false);
                break;
            case Drag.EdgeStart:
                SeekRequested?.Invoke(Edit.MoveEdge(_edgeIndex, startEdge: true, time), false);
                break;
            case Drag.EdgeEnd:
                SeekRequested?.Invoke(Edit.MoveEdge(_edgeIndex, startEdge: false, time), false);
                break;
            case Drag.Select:
                Edit.SetSelection(_selectFrom, time);
                break;
        }

        EditChanged?.Invoke();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        var drag = _drag;
        _drag = Drag.None;
        if (Edit is null || drag == Drag.None)
        {
            return;
        }

        var time = TimeAt(e.GetPosition(this).X);
        if (drag == Drag.Select)
        {
            EditChanged?.Invoke();
            return;
        }

        SeekRequested?.Invoke(drag == Drag.Seek ? time : Position, true);
        EditChanged?.Invoke();
    }
}
