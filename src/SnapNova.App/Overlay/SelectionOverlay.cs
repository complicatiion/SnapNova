using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SnapNova.App.Localization;
using SnapNova.Core.Capture;
using SnapNova.Platform.Windows.Automation;

namespace SnapNova.App.Overlay;

/// <summary>Region (drag), window (click on a highlighted window) or the whole monitor (click on free desktop / Enter).</summary>
public sealed record SelectionResult(MonitorShot Monitor, ScreenRect Area, WindowInfo? Window)
{
    /// <summary>Area in virtual-desktop pixels.</summary>
    public ScreenRect ScreenArea => Area.Offset(Monitor.Bounds.X, Monitor.Bounds.Y);
}

/// <summary>
/// Shows the frozen screen on every monitor (one window per monitor – Avalonia windows spanning monitors with different
/// scaling do not map pixels 1:1) and returns the user's selection. Esc or right click cancels.
/// </summary>
public static class SelectionOverlay
{
    /// <param name="elements">Element snapping while Ctrl is held (null = windows only).</param>
    /// <param name="excludeFromCapture">Second recording: the overlay must not appear in the running video.</param>
    public static Task<SelectionResult?> SelectAsync(FrozenScreen frozen, bool windowOnly, bool forVideo, ElementFinder? elements = null,
        bool scrolling = false, bool excludeFromCapture = false)
    {
        var done = new TaskCompletionSource<SelectionResult?>();
        var windows = new List<OverlayWindow>();

        void Finish(SelectionResult? result)
        {
            if (!done.TrySetResult(result))
            {
                return;
            }

            foreach (var window in windows)
            {
                window.Close();
            }
        }

        foreach (var monitor in frozen.Monitors)
        {
            var window = new OverlayWindow(monitor, frozen, windowOnly, forVideo, elements, scrolling, Finish);
            if (excludeFromCapture && OperatingSystem.IsWindows())
            {
                window.Opened += (_, _) =>
                {
                    if (window.TryGetPlatformHandle()?.Handle is { } hwnd)
                    {
                        SnapNova.Platform.Windows.Shell.WindowTools.ExcludeFromCapture(hwnd);
                    }
                };
            }

            windows.Add(window);
        }

        foreach (var window in windows)
        {
            window.Show();
        }

        // Keyboard focus to the monitor with the cursor.
        var (cx, cy) = OperatingSystem.IsWindows() ? SnapNova.Platform.Windows.Capture.ScreenCapture.CursorPosition() : (0, 0);
        (windows.FirstOrDefault(w => w.Monitor.Bounds.Contains(cx, cy)) ?? windows.FirstOrDefault())?.Activate();
        return done.Task;
    }
}

/// <summary>Borderless topmost window covering one monitor, showing its frozen image.</summary>
internal sealed class OverlayWindow : Window
{
    private readonly WriteableBitmap _bitmap;

    public OverlayWindow(MonitorShot monitor, FrozenScreen frozen, bool windowOnly, bool forVideo, ElementFinder? elements, bool scrolling,
        Action<SelectionResult?> finish)
    {
        Monitor = monitor;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = new Cursor(StandardCursorType.Cross);
        Title = "SnapNova";

        _bitmap = ToBitmap(monitor.Image);
        Content = new SelectionSurface(this, monitor, frozen, _bitmap, windowOnly, forVideo, elements, scrolling, finish);
        PlaceOnMonitor();

        // The real scaling is known once the window lives on its monitor (per-monitor DPI).
        Opened += (_, _) => PlaceOnMonitor();
        PointerEntered += (_, _) => Activate();
        Closed += (_, _) => _bitmap.Dispose();
    }

    public MonitorShot Monitor { get; }

    private void PlaceOnMonitor()
    {
        var scaling = RenderScaling > 0 && IsVisible
            ? RenderScaling
            : Screens.ScreenFromPoint(new PixelPoint(Monitor.Bounds.X + 1, Monitor.Bounds.Y + 1))?.Scaling ?? 1;
        Position = new PixelPoint(Monitor.Bounds.X, Monitor.Bounds.Y);
        Width = Monitor.Bounds.Width / scaling;
        Height = Monitor.Bounds.Height / scaling;
    }

    private static WriteableBitmap ToBitmap(PixelImage image)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        for (var y = 0; y < image.Height; y++)
        {
            Marshal.Copy(image.Pixels, y * image.Stride, buffer.Address + y * buffer.RowBytes, image.Width * 4);
        }

        return bitmap;
    }
}

/// <summary>Draws the frozen screen, the dimming, the selection, the size label and the magnifier.</summary>
internal sealed class SelectionSurface : Control
{
    private const double MinDrag = 4;
    private const int LoupePixels = 15;
    private const double LoupeZoom = 8;

    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#FF8C00"));
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(230, 0x1E, 0x1E, 0x1E));
    private static readonly IPen AccentPen = new Pen(Accent, 2);
    private static readonly IPen GuidePen = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 140, 0)), 1);
    private static readonly IPen LoupeBorder = new Pen(Accent, 2);
    private static readonly IPen LoupeCross = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 140, 0)), 1);
    private static readonly Typeface LabelFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private readonly Window _window;
    private readonly MonitorShot _monitor;
    private readonly FrozenScreen _frozen;
    private readonly Bitmap _image;
    private readonly bool _windowOnly;
    private readonly bool _forVideo;
    private readonly Action<SelectionResult?> _finish;
    private readonly ElementFinder? _elements;
    private readonly bool _scrolling;

    private Point? _pointer;
    private ScreenRect? _hoverElement;
    private bool _elementMode;
    private int _elementRequest;
    private Point? _dragStart;
    private bool _dragging;
    private WindowInfo? _hoverWindow;

    public SelectionSurface(Window window, MonitorShot monitor, FrozenScreen frozen, Bitmap image, bool windowOnly, bool forVideo,
        ElementFinder? elements, bool scrolling, Action<SelectionResult?> finish)
    {
        _elements = elements;
        _scrolling = scrolling;
        _window = window;
        _monitor = monitor;
        _frozen = frozen;
        _image = image;
        _windowOnly = windowOnly;
        _forVideo = forVideo;
        _finish = finish;
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    private double Scale => _window.RenderScaling > 0 ? _window.RenderScaling : 1;

    // ---------------------------------------------------------------- input

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
        if (_dragStart is { } start && !_windowOnly && Distance(start, _pointer.Value) >= MinDrag)
        {
            _dragging = true;
        }

        if (!_dragging)
        {
            var (vx, vy) = ToVirtual(_pointer.Value);
            _hoverWindow = _frozen.WindowAt(vx, vy);
            _elementMode = e.KeyModifiers.HasFlag(KeyModifiers.Control) && _elements is not null && !_windowOnly;
            if (_elementMode && _hoverWindow is { } w)
            {
                _ = QueryElementAsync(w.Handle, vx, vy);
            }
            else
            {
                _hoverElement = null;
            }
        }

        InvalidateVisual();
    }

    /// <summary>UI Automation lookup on a worker thread; only the newest answer is shown.</summary>
    private async Task QueryElementAsync(IntPtr window, int x, int y)
    {
        var request = ++_elementRequest;
        var rect = await _elements!.FindAsync(window, x, y);
        if (request == _elementRequest)
        {
            _hoverElement = rect;
            InvalidateVisual();
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            _elementMode = false;
            _hoverElement = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_dragging)
        {
            _pointer = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            _finish(null);
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            _dragStart = point.Position;
            _dragging = false;
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is not { } start)
        {
            return;
        }

        e.Pointer.Capture(null);
        var end = e.GetPosition(this);
        _dragStart = null;
        if (_dragging)
        {
            _dragging = false;
            var area = ToPixels(start, end);
            if (area.Width >= 4 && area.Height >= 4)
            {
                Complete(area, null);
            }

            InvalidateVisual();
            return;
        }

        CompleteAtPointer(end);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
                _finish(null);
                e.Handled = true;
                break;
            case Key.Enter or Key.Space:
                CompleteAtPointer(_pointer ?? new Point(Bounds.Width / 2, Bounds.Height / 2));
                e.Handled = true;
                break;
        }
    }

    /// <summary>Click: window below the cursor, otherwise the whole monitor.</summary>
    private void CompleteAtPointer(Point position)
    {
        var full0 = new ScreenRect(0, 0, _monitor.Bounds.Width, _monitor.Bounds.Height);
        if (_elementMode && _hoverElement is { } element)
        {
            var local = element.Offset(-_monitor.Bounds.X, -_monitor.Bounds.Y).Intersect(full0);
            if (!local.IsEmpty)
            {
                Complete(local, _hoverWindow);
                return;
            }
        }

        var (vx, vy) = ToVirtual(position);
        var window = _frozen.WindowAt(vx, vy);
        var full = new ScreenRect(0, 0, _monitor.Bounds.Width, _monitor.Bounds.Height);
        var area = window is null ? full : window.Bounds.Offset(-_monitor.Bounds.X, -_monitor.Bounds.Y).Intersect(full);
        Complete(area.IsEmpty ? full : area, window);
    }

    private void Complete(ScreenRect area, WindowInfo? window)
    {
        if (_forVideo)
        {
            area = area.EvenSize();
        }

        _finish(new SelectionResult(_monitor, area, window));
    }

    // ---------------------------------------------------------------- drawing

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawImage(_image, new Rect(0, 0, _image.PixelSize.Width, _image.PixelSize.Height), bounds);

        Rect? highlight = null;
        string? label = null;
        if (_dragging && _dragStart is { } start && _pointer is { } current)
        {
            var area = ToPixels(start, current);
            highlight = ToDip(area);
            label = $"{area.Width} × {area.Height}";
        }
        else if (_pointer is not null)
        {
            if (_elementMode && _hoverElement is { } el)
            {
                var area = el.Offset(-_monitor.Bounds.X, -_monitor.Bounds.Y)
                    .Intersect(new ScreenRect(0, 0, _monitor.Bounds.Width, _monitor.Bounds.Height));
                if (!area.IsEmpty)
                {
                    highlight = ToDip(area);
                    label = $"{Loc.T("OverlayElement")}  ·  {area.Width} × {area.Height}";
                }
            }
            else if (_hoverWindow is { } w)
            {
                var area = w.Bounds.Offset(-_monitor.Bounds.X, -_monitor.Bounds.Y)
                    .Intersect(new ScreenRect(0, 0, _monitor.Bounds.Width, _monitor.Bounds.Height));
                if (!area.IsEmpty)
                {
                    highlight = ToDip(area);
                    label = $"{Shorten(w.ProcessName)}  ·  {area.Width} × {area.Height}";
                }
            }
            else
            {
                highlight = bounds;
            }
        }

        // Dim everything around the selection.
        if (highlight is { } h)
        {
            context.FillRectangle(Dim, new Rect(0, 0, bounds.Width, h.Top));
            context.FillRectangle(Dim, new Rect(0, h.Bottom, bounds.Width, Math.Max(0, bounds.Height - h.Bottom)));
            context.FillRectangle(Dim, new Rect(0, h.Top, h.Left, h.Height));
            context.FillRectangle(Dim, new Rect(h.Right, h.Top, Math.Max(0, bounds.Width - h.Right), h.Height));
            context.DrawRectangle(null, AccentPen, h.Deflate(1));
        }
        else
        {
            context.FillRectangle(Dim, bounds);
        }

        if (_pointer is { } p)
        {
            if (!_dragging)
            {
                context.DrawLine(GuidePen, new Point(0, p.Y), new Point(bounds.Width, p.Y));
                context.DrawLine(GuidePen, new Point(p.X, 0), new Point(p.X, bounds.Height));
            }

            DrawLoupe(context, p, bounds);
        }

        if (highlight is { } hl && label is not null)
        {
            DrawLabel(context, label, new Point(hl.Left, hl.Top), bounds);
        }

        DrawHint(context, bounds);
    }

    private void DrawLoupe(DrawingContext context, Point pointer, Rect bounds)
    {
        var px = (int)Math.Round(pointer.X * Scale);
        var py = (int)Math.Round(pointer.Y * Scale);
        var half = LoupePixels / 2;
        var size = LoupePixels * LoupeZoom;

        // Bottom right of the cursor; flips at the screen edges.
        var x = pointer.X + 24;
        var y = pointer.Y + 24;
        if (x + size > bounds.Width)
        {
            x = pointer.X - 24 - size;
        }

        if (y + size + 26 > bounds.Height)
        {
            y = pointer.Y - 24 - size - 26;
        }

        var target = new Rect(x, y, size, size);
        var source = new Rect(px - half, py - half, LoupePixels, LoupePixels);
        context.FillRectangle(Brushes.Black, target);
        context.DrawImage(_image, source, target);
        var cell = LoupeZoom;
        context.DrawRectangle(null, LoupeCross, new Rect(target.X + half * cell, target.Y + half * cell, cell, cell));
        context.DrawRectangle(null, LoupeBorder, target);

        var (vx, vy) = ToVirtual(pointer);
        DrawLabel(context, $"{vx}, {vy}", new Point(target.X, target.Bottom + 4), bounds, above: false);
    }

    private static void DrawLabel(DrawingContext context, string text, Point anchor, Rect bounds, bool above = true)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 12, Brushes.White);
        var width = formatted.Width + 14;
        var height = formatted.Height + 6;
        var x = Math.Clamp(anchor.X, 0, Math.Max(0, bounds.Width - width));
        var y = above ? anchor.Y - height - 4 : anchor.Y;
        if (y < 0)
        {
            y = anchor.Y + 4;
        }

        context.DrawRectangle(LabelBackground, null, new Rect(x, y, width, height), 4, 4);
        context.DrawText(formatted, new Point(x + 7, y + 3));
    }

    private void DrawHint(DrawingContext context, Rect bounds)
    {
        var text = Loc.T(_windowOnly ? "OverlayHintWindow" : _forVideo ? "OverlayHintVideo" : _scrolling ? "OverlayHintScroll" : "OverlayHintRegion");
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 13, Brushes.White);
        var width = formatted.Width + 28;
        var rect = new Rect((bounds.Width - width) / 2, 18, width, formatted.Height + 14);
        context.DrawRectangle(LabelBackground, new Pen(Accent, 1), rect, 8, 8);
        context.DrawText(formatted, new Point(rect.X + 14, rect.Y + 7));
    }

    // ---------------------------------------------------------------- coordinates

    /// <summary>DIPs of this surface → physical pixels of the monitor (clamped).</summary>
    private ScreenRect ToPixels(Point a, Point b)
    {
        int Px(double v, int max) => Math.Clamp((int)Math.Round(v * Scale), 0, max);
        var w = _monitor.Bounds.Width;
        var h = _monitor.Bounds.Height;
        return ScreenRect.FromPoints(Px(a.X, w), Px(a.Y, h), Px(b.X, w), Px(b.Y, h));
    }

    private Rect ToDip(ScreenRect r) => new(r.X / Scale, r.Y / Scale, r.Width / Scale, r.Height / Scale);

    private (int X, int Y) ToVirtual(Point p) =>
        (_monitor.Bounds.X + (int)Math.Round(p.X * Scale), _monitor.Bounds.Y + (int)Math.Round(p.Y * Scale));

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static string Shorten(string text) => text.Length > 40 ? text[..39] + "…" : text;
}
