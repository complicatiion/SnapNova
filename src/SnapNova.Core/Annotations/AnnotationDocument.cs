using System.Text.Json;
using System.Text.Json.Serialization;
using SnapNova.Core.Capture;
using SnapNova.Core.Ocr;

namespace SnapNova.Core.Annotations;

/// <summary>
/// Editable layer on top of a capture (document.json inside a .snova file). Coordinates are pixels of the ORIGINAL image;
/// the crop only decides which part is exported. Nothing is burned into the original – export renders everything.
/// </summary>
public sealed class AnnotationDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public CaptureInfo? Info { get; set; }

    /// <summary>Exported part of the original (null = everything).</summary>
    public ScreenRect? Crop { get; set; }

    public List<Shape> Shapes { get; set; } = [];

    /// <summary>Next number of the step tool (1, 2, 3 …).</summary>
    public int NextStep { get; set; } = 1;

    /// <summary>Recognized text (Windows OCR) – for "Copy text", Smart Redact and the gallery search.</summary>
    public OcrResult? Ocr { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, AnnotationJson.Options);

    public static AnnotationDocument FromJson(string json) =>
        JsonSerializer.Deserialize<AnnotationDocument>(json, AnnotationJson.Options) ?? new AnnotationDocument();

    /// <summary>Deep copy (undo/redo snapshots).</summary>
    public AnnotationDocument Clone() => FromJson(ToJson());
}

public static class AnnotationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

/// <summary>Axis-aligned bounds in image pixels.</summary>
public readonly record struct ShapeBounds(float X, float Y, float Width, float Height)
{
    public bool Contains(float x, float y, float tolerance = 0) =>
        x >= X - tolerance && y >= Y - tolerance && x <= X + Width + tolerance && y <= Y + Height + tolerance;

    public static ShapeBounds FromPoints(float x1, float y1, float x2, float y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
}

/// <summary>Base of all annotations. Color is 0xAARRGGBB.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ArrowShape), "arrow")]
[JsonDerivedType(typeof(LineShape), "line")]
[JsonDerivedType(typeof(RectangleShape), "rectangle")]
[JsonDerivedType(typeof(EllipseShape), "ellipse")]
[JsonDerivedType(typeof(HighlightShape), "highlight")]
[JsonDerivedType(typeof(PixelateShape), "pixelate")]
[JsonDerivedType(typeof(RedactShape), "redact")]
[JsonDerivedType(typeof(TextShape), "text")]
[JsonDerivedType(typeof(StepShape), "step")]
[JsonDerivedType(typeof(StrokeShape), "stroke")]
public abstract class Shape
{
    public uint Color { get; set; } = 0xFFFF8C00;

    public float StrokeWidth { get; set; } = 4;

    public abstract ShapeBounds GetBounds();

    public abstract void Move(float dx, float dy);

    /// <summary>Clockwise rotation in degrees around the centre of the bounds (only shapes with CanRotate).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Rotation { get; set; }

    [JsonIgnore]
    public virtual bool CanRotate => false;

    /// <summary>Resized with the 8 box handles (arrows and lines use their two end points instead).</summary>
    [JsonIgnore]
    public virtual bool CanResize => true;

    /// <summary>Takes the new (unrotated) bounds after a handle drag.</summary>
    public virtual void SetBounds(ShapeBounds bounds)
    {
    }

    /// <summary>Hit test with a tolerance in image pixels (thin shapes need some slack).</summary>
    public virtual bool HitTest(float x, float y, float tolerance)
    {
        var (lx, ly) = ToLocal(x, y);
        return GetBounds().Contains(lx, ly, tolerance + StrokeWidth / 2);
    }

    /// <summary>Point in the unrotated frame of the shape.</summary>
    public (float X, float Y) ToLocal(float x, float y)
    {
        if (Rotation == 0)
        {
            return (x, y);
        }

        var b = GetBounds();
        var cx = b.X + b.Width / 2;
        var cy = b.Y + b.Height / 2;
        var a = -Rotation * MathF.PI / 180;
        var dx = x - cx;
        var dy = y - cy;
        return (cx + dx * MathF.Cos(a) - dy * MathF.Sin(a), cy + dx * MathF.Sin(a) + dy * MathF.Cos(a));
    }
}

/// <summary>Shapes defined by a start and an end point (arrow, line).</summary>
public abstract class TwoPointShape : Shape
{
    public override bool CanResize => false;

    public float X1 { get; set; }
    public float Y1 { get; set; }
    public float X2 { get; set; }
    public float Y2 { get; set; }

    public override ShapeBounds GetBounds() => ShapeBounds.FromPoints(X1, Y1, X2, Y2);

    public override void Move(float dx, float dy)
    {
        X1 += dx;
        Y1 += dy;
        X2 += dx;
        Y2 += dy;
    }

    /// <summary>Distance from the point to the segment.</summary>
    public override bool HitTest(float x, float y, float tolerance)
    {
        var dx = X2 - X1;
        var dy = Y2 - Y1;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((x - X1) * dx + (y - Y1) * dy) / lengthSquared, 0, 1);
        var px = X1 + t * dx - x;
        var py = Y1 + t * dy - y;
        var limit = tolerance + StrokeWidth;
        return px * px + py * py <= limit * limit;
    }
}

public sealed class ArrowShape : TwoPointShape
{
}

public sealed class LineShape : TwoPointShape
{
}

/// <summary>Shapes defined by a rectangle.</summary>
public abstract class BoxShape : Shape
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }

    public override ShapeBounds GetBounds() => new(X, Y, Width, Height);

    public override void Move(float dx, float dy)
    {
        X += dx;
        Y += dy;
    }

    public override void SetBounds(ShapeBounds bounds)
    {
        X = bounds.X;
        Y = bounds.Y;
        Width = Math.Max(1, bounds.Width);
        Height = Math.Max(1, bounds.Height);
    }

    public void SetFromPoints(float x1, float y1, float x2, float y2)
    {
        var b = ShapeBounds.FromPoints(x1, y1, x2, y2);
        X = b.X;
        Y = b.Y;
        Width = b.Width;
        Height = b.Height;
    }
}

public sealed class RectangleShape : BoxShape
{
    public override bool CanRotate => true;

    public bool Filled { get; set; }
}

public sealed class EllipseShape : BoxShape
{
    public override bool CanRotate => true;

    public bool Filled { get; set; }
}

/// <summary>Box marker of SnapNova 0.0.1–0.0.3 (translucent rectangle). New marks are freehand <see cref="StrokeShape"/>s.</summary>
public sealed class HighlightShape : BoxShape
{
    public override bool CanRotate => true;
}

/// <summary>Freehand stroke: pen (opaque, round) or text marker (wide, translucent, flat ends, multiplied).</summary>
public sealed class StrokeShape : Shape
{
    /// <summary>x0, y0, x1, y1, … in image pixels.</summary>
    public List<float> Points { get; set; } = [];

    public bool Marker { get; set; }

    public override ShapeBounds GetBounds()
    {
        if (Points.Count < 2)
        {
            return default;
        }

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (var i = 0; i + 1 < Points.Count; i += 2)
        {
            minX = Math.Min(minX, Points[i]);
            maxX = Math.Max(maxX, Points[i]);
            minY = Math.Min(minY, Points[i + 1]);
            maxY = Math.Max(maxY, Points[i + 1]);
        }

        return new ShapeBounds(minX, minY, maxX - minX, maxY - minY);
    }

    public override void Move(float dx, float dy)
    {
        for (var i = 0; i + 1 < Points.Count; i += 2)
        {
            Points[i] += dx;
            Points[i + 1] += dy;
        }
    }

    /// <summary>Scales all points from the old into the new bounds.</summary>
    public override void SetBounds(ShapeBounds bounds)
    {
        var old = GetBounds();
        var sx = old.Width < 1 ? 1 : bounds.Width / old.Width;
        var sy = old.Height < 1 ? 1 : bounds.Height / old.Height;
        for (var i = 0; i + 1 < Points.Count; i += 2)
        {
            Points[i] = bounds.X + (Points[i] - old.X) * sx;
            Points[i + 1] = bounds.Y + (Points[i + 1] - old.Y) * sy;
        }
    }

    /// <summary>Close to any segment of the stroke.</summary>
    public override bool HitTest(float x, float y, float tolerance)
    {
        var limit = tolerance + StrokeWidth / 2;
        for (var i = 0; i + 3 < Points.Count; i += 2)
        {
            if (SegmentDistanceSquared(x, y, Points[i], Points[i + 1], Points[i + 2], Points[i + 3]) <= limit * limit)
            {
                return true;
            }
        }

        return Points.Count == 2 && SegmentDistanceSquared(x, y, Points[0], Points[1], Points[0], Points[1]) <= limit * limit;
    }

    internal static float SegmentDistanceSquared(float x, float y, float x1, float y1, float x2, float y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((x - x1) * dx + (y - y1) * dy) / lengthSquared, 0, 1);
        var px = x1 + t * dx - x;
        var py = y1 + t * dy - y;
        return px * px + py * py;
    }
}

/// <summary>Pixelates the content below (irreversible in the export – the safe way to hide data).</summary>
public sealed class PixelateShape : BoxShape
{
    public int BlockSize { get; set; } = 12;
}

public enum RedactMode
{
    /// <summary>Strongly downscaled first (information gone), then drawn soft with feathered edges.</summary>
    Blur,
    Pixelate,
    /// <summary>Solid black bar.</summary>
    Bar
}

/// <summary>Hides content irreversibly in the export (Smart Redact and the redact tool).</summary>
public sealed class RedactShape : BoxShape
{
    public RedactMode Mode { get; set; } = RedactMode.Blur;
}

/// <summary>Background behind a text.</summary>
public enum TextPlate
{
    /// <summary>Transparent (default): text only, with a soft shadow for readability.</summary>
    None,
    Black,
    White,

    /// <summary>Own colour (<see cref="TextShape.PlateColor"/>).</summary>
    Custom,

    /// <summary>Dark or light depending on the text colour (texts of SnapNova 0.0.1–0.0.5).</summary>
    Auto
}

public sealed class TextShape : Shape
{
    /// <summary>Exact text size from the renderer (set by the app; without it the bounds are estimated).</summary>
    public static Func<TextShape, (float Width, float Height)>? Measurer { get; set; }

    public override bool CanRotate => true;

    /// <summary>Background behind the text. Older files only have <see cref="Background"/> (= Auto).</summary>
    public TextPlate Plate { get; set; }

    public uint PlateColor { get; set; } = 0xFF1E1E1E;

    /// <summary>The plate actually drawn (old "background: true" files keep their label).</summary>
    [JsonIgnore]
    public TextPlate EffectivePlate => Plate == TextPlate.None && Background ? TextPlate.Auto : Plate;

    /// <summary>Resizing the box scales the font.</summary>
    public override void SetBounds(ShapeBounds bounds)
    {
        var old = GetBounds();
        if (old.Height > 0)
        {
            FontSize = Math.Clamp(FontSize * bounds.Height / old.Height, 8, 400);
        }

        X = bounds.X;
        Y = bounds.Y;
    }

    public float X { get; set; }
    public float Y { get; set; }
    public string Text { get; set; } = "";
    public float FontSize { get; set; } = 28;

    /// <summary>Legacy (0.0.1–0.0.5): text on a filled label. New texts use <see cref="Plate"/>.</summary>
    public bool Background { get; set; }

    /// <summary>Bounds of the label: measured by the renderer when available, otherwise estimated.</summary>
    public override ShapeBounds GetBounds()
    {
        if (Measurer is not null && Text.Length > 0)
        {
            var (w, h) = Measurer(this);
            return new ShapeBounds(X, Y, w, h);
        }

        var lines = Text.Split('\n');
        var width = lines.Max(l => l.Length) * FontSize * 0.58f + FontSize * 0.6f;
        var height = lines.Length * FontSize * 1.3f + FontSize * 0.3f;
        return new ShapeBounds(X, Y, Math.Max(width, FontSize), height);
    }

    public override void Move(float dx, float dy)
    {
        X += dx;
        Y += dy;
    }
}

/// <summary>Numbered circle for step-by-step guides.</summary>
public sealed class StepShape : Shape
{
    public float X { get; set; }
    public float Y { get; set; }
    public int Number { get; set; } = 1;
    public float Radius { get; set; } = 18;

    public override ShapeBounds GetBounds() => new(X - Radius, Y - Radius, Radius * 2, Radius * 2);

    public override void SetBounds(ShapeBounds bounds)
    {
        Radius = Math.Clamp(Math.Max(bounds.Width, bounds.Height) / 2, 8, 200);
        X = bounds.X + bounds.Width / 2;
        Y = bounds.Y + bounds.Height / 2;
    }

    public override void Move(float dx, float dy)
    {
        X += dx;
        Y += dy;
    }
}
