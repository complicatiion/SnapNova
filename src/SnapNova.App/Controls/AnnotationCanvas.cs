using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using SnapNova.App.Rendering;
using SnapNova.App.ViewModels;
using SnapNova.Core.Annotations;
using SnapNova.Core.Capture;

namespace SnapNova.App.Controls;

/// <summary>
/// The editor surface: draws the capture and its annotations with the same Skia renderer as the export, and turns
/// pointer input into shapes. Coordinates of the model are pixels of the original image.
/// <list type="bullet">
/// <item>Mouse wheel zooms at the cursor; drag on free space with the select tool, Space + drag or the middle button pans.</item>
/// <item>A click on an existing shape selects and moves it with every tool – new shapes start only on free space.</item>
/// <item>The selected shape has handles: 8 for size, one for rotation, end points for arrows and lines.</item>
/// </list>
/// </summary>
public sealed class AnnotationCanvas : Control
{
    private const float HitTolerance = 6;
    private const double HandleSize = 9;
    private const double RotateDistance = 26;

    private static readonly double[] ZoomSteps = [0.05, 0.1, 0.15, 0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.25, 1.5, 2, 3, 4, 6, 8, 12, 16];

    private enum DragMode
    {
        None,
        Pan,
        Draw,
        Freehand,
        Move,
        Handle,
        Crop
    }

    /// <summary>Hx/Hy ∈ {-1, 0, 1}: position on the box relative to its centre; End 1/2: end point of an arrow or line.</summary>
    private readonly record struct Handle(int Hx, int Hy, bool Rotate, int End);

    private EditorViewModel? _vm;
    private DragMode _drag;
    private Handle _handle;
    private SKPoint _startImage;
    private SKPoint _lastImage;
    private bool _changeStarted;
    private Vector _pan;
    private Point _panStart;
    private Vector _panAtStart;
    private StrokeShape? _stroke;

    // Handle drag: state at the start.
    private ShapeBounds _startBounds;
    private float _startRotation;
    private SKPoint _anchorWorld;

    public AnnotationCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    /// <summary>Text tool click or double-click on a text: the window shows an input box at this view position.</summary>
    public event Action<TextShape?, SKPoint, Point>? TextRequested;

    /// <summary>Zoom or pan changed (status line).</summary>
    public event Action? ViewChanged;

    /// <summary>Held Space: every tool pans (set by the window).</summary>
    public bool SpacePan { get; set; }

    public EditorViewModel? ViewModel
    {
        get => _vm;
        set
        {
            if (_vm is not null)
            {
                _vm.Invalidated -= InvalidateVisual;
                _vm.ZoomStepRequested -= ZoomStep;
                _vm.ViewResetRequested -= ResetPan;
            }

            _vm = value;
            if (_vm is not null)
            {
                _vm.Invalidated += InvalidateVisual;
                _vm.ZoomStepRequested += ZoomStep;
                _vm.ViewResetRequested += ResetPan;
            }

            InvalidateVisual();
        }
    }

    /// <summary>Current scale (view DIPs per image pixel).</summary>
    public double ViewScale
    {
        get
        {
            if (_vm is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            {
                return 1;
            }

            if (_vm.Zoom > 0)
            {
                return _vm.Zoom;
            }

            // Fit, but never enlarge small captures (they would look blurry).
            var fit = Math.Min((Bounds.Width - 32) / _vm.Source.Width, (Bounds.Height - 32) / _vm.Source.Height);
            return Math.Clamp(fit, 0.05, 1);
        }
    }

    private Point Origin
    {
        get
        {
            if (_vm is null)
            {
                return default;
            }

            var s = ViewScale;
            return new Point((Bounds.Width - _vm.Source.Width * s) / 2 + _pan.X, (Bounds.Height - _vm.Source.Height * s) / 2 + _pan.Y);
        }
    }

    public SKPoint ToImage(Point view)
    {
        var o = Origin;
        var s = ViewScale;
        return new SKPoint((float)((view.X - o.X) / s), (float)((view.Y - o.Y) / s));
    }

    public Point ToView(float x, float y)
    {
        var o = Origin;
        var s = ViewScale;
        return new Point(o.X + x * s, o.Y + y * s);
    }

    // ---------------------------------------------------------------- zoom and pan

    /// <summary>Zooms so that the image point under <paramref name="view"/> stays where it is.</summary>
    public void ZoomAt(Point view, double scale)
    {
        if (_vm is null)
        {
            return;
        }

        var image = ToImage(view);
        scale = Math.Clamp(scale, 0.05, 16);
        _vm.Zoom = scale;
        _pan = new Vector(
            view.X - image.X * scale - (Bounds.Width - _vm.Source.Width * scale) / 2,
            view.Y - image.Y * scale - (Bounds.Height - _vm.Source.Height * scale) / 2);
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>Next zoom step (buttons, Ctrl + / -), centred on the view.</summary>
    private void ZoomStep(int direction)
    {
        var current = ViewScale;
        var next = direction > 0
            ? ZoomSteps.FirstOrDefault(z => z > current + 0.001, 16)
            : ZoomSteps.LastOrDefault(z => z < current - 0.001, 0.05);
        ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), next);
    }

    /// <summary>Back to the centred view (zoom fit / 100 %).</summary>
    public void ResetPan()
    {
        _pan = default;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_vm is null || e.Delta.Y == 0)
        {
            return;
        }

        ZoomAt(e.GetPosition(this), ViewScale * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15));
        e.Handled = true;
    }

    // ---------------------------------------------------------------- drawing

    public override void Render(DrawingContext context)
    {
        if (_vm is null)
        {
            return;
        }

        // Transparent fill: makes the control hit-testable everywhere (pointer events for all tools).
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        context.Custom(new DrawOperation(new Rect(Bounds.Size), Snapshot(_vm), Origin, ViewScale));
    }

    /// <summary>Immutable copy for the render thread (shapes are cloned; the source bitmap is read-only).</summary>
    private static Frame Snapshot(EditorViewModel vm)
    {
        var doc = vm.Document.Clone();
        Shape? selected = null;
        if (vm.Selected is { } s)
        {
            var index = vm.Document.Shapes.IndexOf(s);
            selected = index >= 0 ? doc.Shapes[index] : null;
        }

        var preview = vm.Preview is null ? null : AnnotationDocument.FromJson(new AnnotationDocument { Shapes = [vm.Preview] }.ToJson()).Shapes[0];
        return new Frame(vm.Source, doc.Shapes, preview, selected, doc.Crop, vm.CropPreview, vm.Tool == EditorTool.Crop);
    }

    private sealed record Frame(SKBitmap Source, List<Shape> Shapes, Shape? Preview, Shape? Selected, ScreenRect? Crop, SKRectI? CropPreview,
        bool CropMode);

    private sealed class DrawOperation(Rect bounds, Frame frame, Point origin, double scale) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        // The whole surface takes the mouse – otherwise no tool receives pointer events.
        public bool HitTest(Point p) => bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
            {
                return;
            }

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            canvas.Translate((float)origin.X, (float)origin.Y);
            canvas.Scale((float)scale);

            // Soft shadow under the image.
            using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 90), ImageFilter = SKImageFilter.CreateBlur(12, 12) })
            {
                canvas.DrawRect(SKRect.Create(0, 4, frame.Source.Width, frame.Source.Height), shadow);
            }

            using (var sampling = new SKPaint { FilterQuality = scale < 1 ? SKFilterQuality.Medium : SKFilterQuality.None })
            {
                canvas.DrawBitmap(frame.Source, 0, 0, sampling);
            }

            AnnotationRenderer.Draw(canvas, frame.Source, frame.Shapes, drawSource: false);
            if (frame.Preview is not null)
            {
                AnnotationRenderer.DrawShape(canvas, frame.Source, frame.Preview);
            }

            var px = (float)(1 / scale);
            if (frame.Selected is { } sel && !frame.CropMode)
            {
                DrawSelection(canvas, sel, px);
            }

            DrawCrop(canvas, px);
            canvas.Restore();
        }

        private static void DrawSelection(SKCanvas canvas, Shape sel, float px)
        {
            using var line = new SKPaint
            {
                Color = new SKColor(0xFF, 0x8C, 0x00),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f * px,
                IsAntialias = true
            };
            using var fill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
            var h = (float)HandleSize * px;

            if (sel is TwoPointShape tp)
            {
                foreach (var (x, y) in new[] { (tp.X1, tp.Y1), (tp.X2, tp.Y2) })
                {
                    canvas.DrawCircle(x, y, h * 0.65f, fill);
                    canvas.DrawCircle(x, y, h * 0.65f, line);
                }

                return;
            }

            var b = sel.GetBounds();
            var pad = 4 * px + sel.StrokeWidth / 2;
            var rect = SKRect.Create(b.X - pad, b.Y - pad, b.Width + pad * 2, b.Height + pad * 2);
            canvas.Save();
            if (sel.CanRotate && sel.Rotation != 0)
            {
                canvas.RotateDegrees(sel.Rotation, b.X + b.Width / 2, b.Y + b.Height / 2);
            }

            using (var dash = new SKPaint
                   {
                       Color = line.Color,
                       Style = SKPaintStyle.Stroke,
                       StrokeWidth = 1.5f * px,
                       IsAntialias = true,
                       PathEffect = SKPathEffect.CreateDash([6 * px, 4 * px], 0)
                   })
            {
                canvas.DrawRect(rect, dash);
            }

            if (sel.CanResize)
            {
                for (var hy = -1; hy <= 1; hy++)
                {
                    for (var hx = -1; hx <= 1; hx++)
                    {
                        if (hx == 0 && hy == 0)
                        {
                            continue;
                        }

                        var cx = rect.MidX + hx * rect.Width / 2;
                        var cy = rect.MidY + hy * rect.Height / 2;
                        var r = SKRect.Create(cx - h / 2, cy - h / 2, h, h);
                        canvas.DrawRect(r, fill);
                        canvas.DrawRect(r, line);
                    }
                }
            }

            if (sel.CanRotate)
            {
                var top = rect.Top;
                var knob = top - (float)RotateDistance * px;
                canvas.DrawLine(rect.MidX, top, rect.MidX, knob, line);
                canvas.DrawCircle(rect.MidX, knob, h * 0.6f, fill);
                canvas.DrawCircle(rect.MidX, knob, h * 0.6f, line);
            }

            canvas.Restore();
        }

        private void DrawCrop(SKCanvas canvas, float px)
        {
            SKRect? crop = frame.CropPreview is { } p ? SKRect.Create(p.Left, p.Top, p.Width, p.Height)
                : frame.Crop is { IsEmpty: false } c ? SKRect.Create(c.X, c.Y, c.Width, c.Height)
                : null;
            if (crop is not { } r)
            {
                return;
            }

            // Outside the crop: dimmed (stronger while the crop tool is active).
            using var dim = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(frame.CropMode ? 150 : 110)) };
            float w = frame.Source.Width, h = frame.Source.Height;
            canvas.DrawRect(SKRect.Create(0, 0, w, r.Top), dim);
            canvas.DrawRect(SKRect.Create(0, r.Bottom, w, h - r.Bottom), dim);
            canvas.DrawRect(SKRect.Create(0, r.Top, r.Left, r.Height), dim);
            canvas.DrawRect(SKRect.Create(r.Right, r.Top, w - r.Right, r.Height), dim);
            using var border = new SKPaint { Color = new SKColor(0xFF, 0x8C, 0x00), Style = SKPaintStyle.Stroke, StrokeWidth = 2 * px };
            canvas.DrawRect(r, border);
        }
    }

    // ---------------------------------------------------------------- input

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_vm is null)
        {
            return;
        }

        Focus();
        var point = e.GetCurrentPoint(this);
        var img = ToImage(point.Position);
        _startImage = img;
        _lastImage = img;
        _changeStarted = false;

        if (point.Properties.IsMiddleButtonPressed
            || (point.Properties.IsLeftButtonPressed && (SpacePan || e.KeyModifiers.HasFlag(KeyModifiers.Alt))))
        {
            BeginPan(point.Position, e);
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Pointer.Capture(this);

        if (_vm.Tool == EditorTool.Crop)
        {
            _drag = DragMode.Crop;
            return;
        }

        // 1. A handle of the selected shape.
        if (_vm.Selected is { } selected && HandleAt(selected, point.Position) is { } handle)
        {
            BeginHandle(selected, handle);
            return;
        }

        // 2. An existing shape: select it (double-click on a text edits it).
        var hit = HitTest(img);
        // Texts behave like every other shape: click selects and moves, double-click edits.
        if (hit is TextShape text && e.ClickCount == 2)
        {
            _drag = DragMode.None;
            _vm.Selected = text;
            TextRequested?.Invoke(text, new SKPoint(text.X, text.Y), ToView(text.X, text.Y));
            return;
        }

        if (hit is not null)
        {
            _vm.Selected = hit;
            _drag = DragMode.Move;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            return;
        }

        // 3. Free space.
        _vm.Selected = null;
        switch (_vm.Tool)
        {
            case EditorTool.Select:
                BeginPan(point.Position, e);
                break;
            case EditorTool.Text:
                _drag = DragMode.None;
                TextRequested?.Invoke(null, img, point.Position);
                break;
            case EditorTool.Step:
                _drag = DragMode.None;
                _vm.AddShape(new StepShape
                {
                    X = img.X,
                    Y = img.Y,
                    Number = _vm.TakeStepNumber(),
                    Radius = 10 + _vm.StrokeWidth * 2,
                    Color = _vm.Color
                });
                break;
            case EditorTool.Pen or EditorTool.Highlight:
                _drag = DragMode.Freehand;
                _stroke = new StrokeShape
                {
                    Marker = _vm.Tool == EditorTool.Highlight,
                    Color = _vm.Color,
                    StrokeWidth = _vm.Tool == EditorTool.Highlight ? 8 + _vm.StrokeWidth * 3 : _vm.StrokeWidth,
                    Points = [img.X, img.Y]
                };
                _vm.Preview = _stroke;
                _vm.Invalidate();
                break;
            default:
                _drag = DragMode.Draw;
                break;
        }
    }

    private void BeginPan(Point position, PointerPressedEventArgs e)
    {
        _drag = DragMode.Pan;
        _panStart = position;
        _panAtStart = _pan;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_vm is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        var img = ToImage(position);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (_drag)
        {
            case DragMode.None:
                UpdateHoverCursor(position, img);
                return;
            case DragMode.Pan:
                _pan = _panAtStart + (position - _panStart);
                InvalidateVisual();
                ViewChanged?.Invoke();
                return;
            case DragMode.Crop:
                _vm.CropPreview = ClampToImage(_startImage, img);
                _vm.Invalidate();
                return;
            case DragMode.Move when _vm.Selected is { } shape:
                StartChangeOnce();
                shape.Move(img.X - _lastImage.X, img.Y - _lastImage.Y);
                _lastImage = img;
                _vm.Invalidate();
                return;
            case DragMode.Handle when _vm.Selected is { } shape:
                StartChangeOnce();
                DragHandle(shape, img, shift);
                _vm.Invalidate();
                return;
            case DragMode.Freehand when _stroke is not null:
                AddStrokePoint(img, shift);
                _vm.Invalidate();
                return;
            case DragMode.Draw:
                _vm.Preview = CreateShape(_vm, _startImage, Constrain(_vm.Tool, _startImage, img, shift));
                _vm.Invalidate();
                return;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        if (_vm is null)
        {
            return;
        }

        var drag = _drag;
        _drag = DragMode.None;
        var img = ToImage(e.GetPosition(this));
        UpdateHoverCursor(e.GetPosition(this), img);

        switch (drag)
        {
            case DragMode.Crop:
                var crop = ClampToImage(_startImage, img);
                _vm.CropPreview = null;
                if (crop.Width >= 4 && crop.Height >= 4)
                {
                    _vm.SetCrop(new ScreenRect(crop.Left, crop.Top, crop.Width, crop.Height));
                }
                else
                {
                    _vm.Invalidate();
                }

                break;
            case DragMode.Freehand when _stroke is { } stroke:
                _stroke = null;
                _vm.Preview = null;
                _vm.AddShape(stroke);
                break;
            case DragMode.Draw:
                _vm.Preview = null;
                var end = Constrain(_vm.Tool, _startImage, img, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                if (Math.Abs(end.X - _startImage.X) < 3 && Math.Abs(end.Y - _startImage.Y) < 3)
                {
                    _vm.Invalidate();
                }
                else if (CreateShape(_vm, _startImage, end) is { } shape)
                {
                    _vm.AddShape(shape);
                }

                break;
            case DragMode.Move or DragMode.Handle:
                _vm.Invalidate();
                break;
        }
    }

    /// <summary>The undo snapshot is taken on the first real movement (a plain click changes nothing).</summary>
    private void StartChangeOnce()
    {
        if (!_changeStarted)
        {
            _changeStarted = true;
            _vm!.BeginChange();
        }
    }

    private void AddStrokePoint(SKPoint img, bool straight)
    {
        var points = _stroke!.Points;
        if (straight)
        {
            // Shift: straight mark from the start point (45° steps).
            var end = Constrain(EditorTool.Line, new SKPoint(points[0], points[1]), img, true);
            points.RemoveRange(2, points.Count - 2);
            points.Add(end.X);
            points.Add(end.Y);
            return;
        }

        var lx = points[^2];
        var ly = points[^1];
        var min = (float)(1.5 / ViewScale);
        if (Math.Abs(img.X - lx) + Math.Abs(img.Y - ly) >= min)
        {
            points.Add(img.X);
            points.Add(img.Y);
        }
    }

    // ---------------------------------------------------------------- handles

    private static (float X, float Y) Rotate(float x, float y, float cx, float cy, float degrees)
    {
        if (degrees == 0)
        {
            return (x, y);
        }

        var a = degrees * MathF.PI / 180;
        var dx = x - cx;
        var dy = y - cy;
        return (cx + dx * MathF.Cos(a) - dy * MathF.Sin(a), cy + dx * MathF.Sin(a) + dy * MathF.Cos(a));
    }

    /// <summary>The handle under the view point (in view pixels, independent of the zoom).</summary>
    private Handle? HandleAt(Shape shape, Point view)
    {
        var grab = HandleSize / 2 + 3;
        bool Near(float x, float y)
        {
            var p = ToView(x, y);
            return Math.Abs(p.X - view.X) <= grab && Math.Abs(p.Y - view.Y) <= grab;
        }

        if (shape is TwoPointShape tp)
        {
            return Near(tp.X2, tp.Y2) ? new Handle(0, 0, false, 2) : Near(tp.X1, tp.Y1) ? new Handle(0, 0, false, 1) : null;
        }

        var b = shape.GetBounds();
        var pad = (float)(4 / ViewScale) + shape.StrokeWidth / 2;
        var cx = b.X + b.Width / 2;
        var cy = b.Y + b.Height / 2;
        var halfW = b.Width / 2 + pad;
        var halfH = b.Height / 2 + pad;
        var rotation = shape.CanRotate ? shape.Rotation : 0;

        if (shape.CanRotate)
        {
            var (rx, ry) = Rotate(cx, cy - halfH - (float)(RotateDistance / ViewScale), cx, cy, rotation);
            if (Near(rx, ry))
            {
                return new Handle(0, 0, true, 0);
            }
        }

        if (!shape.CanResize)
        {
            return null;
        }

        for (var hy = -1; hy <= 1; hy++)
        {
            for (var hx = -1; hx <= 1; hx++)
            {
                if ((hx != 0 || hy != 0) && Rotate(cx + hx * halfW, cy + hy * halfH, cx, cy, rotation) is var (x, y) && Near(x, y))
                {
                    return new Handle(hx, hy, false, 0);
                }
            }
        }

        return null;
    }

    private void BeginHandle(Shape shape, Handle handle)
    {
        _drag = DragMode.Handle;
        _handle = handle;
        _startBounds = shape.GetBounds();
        _startRotation = shape.CanRotate ? shape.Rotation : 0;
        var b = _startBounds;
        var cx = b.X + b.Width / 2;
        var cy = b.Y + b.Height / 2;

        // The opposite handle stays where it is while resizing.
        var (ax, ay) = Rotate(cx - handle.Hx * b.Width / 2, cy - handle.Hy * b.Height / 2, cx, cy, _startRotation);
        _anchorWorld = new SKPoint(ax, ay);
    }

    private void DragHandle(Shape shape, SKPoint img, bool shift)
    {
        if (shape is TwoPointShape tp)
        {
            var fixedEnd = _handle.End == 2 ? new SKPoint(tp.X1, tp.Y1) : new SKPoint(tp.X2, tp.Y2);
            var p = Constrain(EditorTool.Line, fixedEnd, img, shift);
            if (_handle.End == 2)
            {
                (tp.X2, tp.Y2) = (p.X, p.Y);
            }
            else
            {
                (tp.X1, tp.Y1) = (p.X, p.Y);
            }

            return;
        }

        var b = _startBounds;
        var cx = b.X + b.Width / 2;
        var cy = b.Y + b.Height / 2;

        if (_handle.Rotate)
        {
            var angle = MathF.Atan2(img.Y - cy, img.X - cx) * 180 / MathF.PI + 90;
            if (shift)
            {
                angle = MathF.Round(angle / 15) * 15; // Shift: 15° steps
            }

            shape.Rotation = (angle % 360 + 360) % 360;
            return;
        }

        // Pointer relative to the fixed anchor, in the unrotated frame of the shape.
        var (dx, dy) = Rotate(img.X - _anchorWorld.X, img.Y - _anchorWorld.Y, 0, 0, -_startRotation);
        float x0, x1, y0, y1;
        if (_handle.Hx == 0)
        {
            (x0, x1) = (-b.Width / 2, b.Width / 2);
        }
        else
        {
            (x0, x1) = (Math.Min(0, dx), Math.Max(0, dx));
        }

        if (_handle.Hy == 0)
        {
            (y0, y1) = (-b.Height / 2, b.Height / 2);
        }
        else
        {
            (y0, y1) = (Math.Min(0, dy), Math.Max(0, dy));
        }

        var w = Math.Max(2, x1 - x0);
        var h = Math.Max(2, y1 - y0);
        if ((shift || shape is TextShape or StepShape) && _handle.Hx != 0 && _handle.Hy != 0 && b.Width > 0 && b.Height > 0)
        {
            // Corner with Shift (text and steps always): keep the aspect ratio.
            var scale = Math.Max(w / b.Width, h / b.Height);
            w = b.Width * scale;
            h = b.Height * scale;
            x0 = dx < 0 ? -w : 0;
            y0 = dy < 0 ? -h : 0;
        }

        // Centre of the new box in the world → unrotated bounds.
        var (mx, my) = Rotate(x0 + w / 2, y0 + h / 2, 0, 0, _startRotation);
        var centerX = _anchorWorld.X + mx;
        var centerY = _anchorWorld.Y + my;
        shape.SetBounds(new ShapeBounds(centerX - w / 2, centerY - h / 2, w, h));
    }

    private void UpdateHoverCursor(Point view, SKPoint img)
    {
        if (_vm is null)
        {
            return;
        }

        StandardCursorType type;
        if (SpacePan)
        {
            type = StandardCursorType.Hand;
        }
        else if (_vm.Tool == EditorTool.Crop)
        {
            type = StandardCursorType.Cross;
        }
        else if (_vm.Selected is { } sel && HandleAt(sel, view) is { } handle)
        {
            type = handle.Rotate ? StandardCursorType.Hand
                : handle.End != 0 ? StandardCursorType.Cross
                : (handle.Hx == 0) ? StandardCursorType.SizeNorthSouth
                : (handle.Hy == 0) ? StandardCursorType.SizeWestEast
                : (handle.Hx == handle.Hy) ? StandardCursorType.TopLeftCorner
                : StandardCursorType.TopRightCorner;
        }
        else if (HitTest(img) is not null)
        {
            type = StandardCursorType.SizeAll;
        }
        else
        {
            type = _vm.Tool switch
            {
                EditorTool.Select => StandardCursorType.Hand,
                EditorTool.Text => StandardCursorType.Ibeam,
                _ => StandardCursorType.Cross
            };
        }

        if (_hoverCursor != type)
        {
            _hoverCursor = type;
            Cursor = new Cursor(type);
        }
    }

    private StandardCursorType? _hoverCursor;

    private Shape? HitTest(SKPoint img)
    {
        var tolerance = (float)(HitTolerance / ViewScale);
        for (var i = _vm!.Document.Shapes.Count - 1; i >= 0; i--)
        {
            if (_vm.Document.Shapes[i].HitTest(img.X, img.Y, tolerance))
            {
                return _vm.Document.Shapes[i];
            }
        }

        return null;
    }

    private SKRectI ClampToImage(SKPoint a, SKPoint b)
    {
        int Cx(float v) => Math.Clamp((int)Math.Round(v), 0, _vm!.Source.Width);
        int Cy(float v) => Math.Clamp((int)Math.Round(v), 0, _vm!.Source.Height);
        return new SKRectI(Math.Min(Cx(a.X), Cx(b.X)), Math.Min(Cy(a.Y), Cy(b.Y)), Math.Max(Cx(a.X), Cx(b.X)), Math.Max(Cy(a.Y), Cy(b.Y)));
    }

    /// <summary>Shift: lines in 45° steps, boxes square.</summary>
    private static SKPoint Constrain(EditorTool tool, SKPoint start, SKPoint end, bool shift)
    {
        if (!shift)
        {
            return end;
        }

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (tool is EditorTool.Arrow or EditorTool.Line)
        {
            var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
            var length = Math.Sqrt(dx * dx + dy * dy);
            return new SKPoint(start.X + (float)(Math.Cos(angle) * length), start.Y + (float)(Math.Sin(angle) * length));
        }

        var size = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new SKPoint(start.X + Math.Sign(dx) * size, start.Y + Math.Sign(dy) * size);
    }

    private static Shape? CreateShape(EditorViewModel vm, SKPoint a, SKPoint b)
    {
        Shape? shape = vm.Tool switch
        {
            EditorTool.Arrow => new ArrowShape { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y },
            EditorTool.Line => new LineShape { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y },
            EditorTool.Rectangle => Box(new RectangleShape(), a, b),
            EditorTool.Ellipse => Box(new EllipseShape(), a, b),
            EditorTool.Redact => Box(new RedactShape { Mode = vm.RedactMode }, a, b),
            _ => null
        };
        if (shape is not null)
        {
            shape.Color = vm.Color;
            shape.StrokeWidth = vm.StrokeWidth;
        }

        return shape;
    }

    private static T Box<T>(T box, SKPoint a, SKPoint b)
        where T : BoxShape
    {
        box.SetFromPoints(a.X, a.Y, b.X, b.Y);
        return box;
    }
}
