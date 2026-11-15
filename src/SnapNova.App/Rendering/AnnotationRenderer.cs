using System.Runtime.InteropServices;
using SkiaSharp;
using SnapNova.Core.Annotations;
using SnapNova.Core.Capture;

namespace SnapNova.App.Rendering;

/// <summary>
/// The one renderer for annotations: the editor draws with it on screen and the export burns the same drawing into
/// the image – what you see is exactly what you get. Coordinates are pixels of the original image.
/// </summary>
public static class AnnotationRenderer
{
    private static readonly SKTypeface TextFace =
        SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;

    /// <summary>Draws the source image (optional) and all shapes. The canvas transform maps image pixels.</summary>
    public static void Draw(SKCanvas canvas, SKBitmap source, IEnumerable<Shape> shapes, bool drawSource = true)
    {
        if (drawSource)
        {
            canvas.DrawBitmap(source, 0, 0);
        }

        foreach (var shape in shapes)
        {
            DrawShape(canvas, source, shape);
        }
    }

    public static void DrawShape(SKCanvas canvas, SKBitmap source, Shape shape)
    {
        // Rotated shapes: rotate the canvas around the centre of the (unrotated) bounds.
        var rotated = shape.CanRotate && shape.Rotation != 0;
        if (rotated)
        {
            var b = shape.GetBounds();
            canvas.Save();
            canvas.RotateDegrees(shape.Rotation, b.X + b.Width / 2, b.Y + b.Height / 2);
        }

        DrawShapeCore(canvas, source, shape);
        if (rotated)
        {
            canvas.Restore();
        }
    }

    private static void DrawShapeCore(SKCanvas canvas, SKBitmap source, Shape shape)
    {
        var color = new SKColor(shape.Color);
        switch (shape)
        {
            case PixelateShape p:
                Pixelate(canvas, source, SKRectI.Round(Rect(p)), p.BlockSize);
                break;
            case RedactShape r:
                Redact(canvas, source, r);
                break;
            case HighlightShape h:
                using (var paint = new SKPaint { Color = color.WithAlpha(110), IsAntialias = false, BlendMode = SKBlendMode.Multiply })
                {
                    canvas.DrawRect(Rect(h), paint);
                }

                break;
            case RectangleShape r:
                using (var paint = Stroke(color, r.StrokeWidth))
                {
                    if (r.Filled)
                    {
                        paint.Style = SKPaintStyle.Fill;
                    }

                    canvas.DrawRoundRect(Rect(r), 3, 3, paint);
                }

                break;
            case EllipseShape e:
                using (var paint = Stroke(color, e.StrokeWidth))
                {
                    if (e.Filled)
                    {
                        paint.Style = SKPaintStyle.Fill;
                    }

                    canvas.DrawOval(Rect(e), paint);
                }

                break;
            case ArrowShape a:
                DrawArrow(canvas, a, color);
                break;
            case LineShape l:
                using (var paint = Stroke(color, l.StrokeWidth))
                {
                    canvas.DrawLine(l.X1, l.Y1, l.X2, l.Y2, paint);
                }

                break;
            case TextShape t:
                DrawText(canvas, t, color);
                break;
            case StepShape s:
                DrawStep(canvas, s, color);
                break;
            case StrokeShape stroke:
                DrawStroke(canvas, stroke, color);
                break;
        }
    }

    /// <summary>Freehand: pen (opaque, round) or marker (translucent, flat ends, multiplied so text stays readable).</summary>
    private static void DrawStroke(SKCanvas canvas, StrokeShape s, SKColor color)
    {
        if (s.Points.Count < 2)
        {
            return;
        }

        using var path = new SKPath();
        path.MoveTo(s.Points[0], s.Points[1]);
        if (s.Points.Count == 2)
        {
            path.LineTo(s.Points[0] + 0.1f, s.Points[1]);
        }

        // Smoothed with quadratic curves through the midpoints of the samples.
        for (var i = 2; i + 1 < s.Points.Count; i += 2)
        {
            var x = s.Points[i];
            var y = s.Points[i + 1];
            if (i + 3 < s.Points.Count)
            {
                path.QuadTo(x, y, (x + s.Points[i + 2]) / 2, (y + s.Points[i + 3]) / 2);
            }
            else
            {
                path.LineTo(x, y);
            }
        }

        using var paint = Stroke(s.Marker ? color.WithAlpha(120) : color, s.StrokeWidth);
        if (s.Marker)
        {
            paint.StrokeCap = SKStrokeCap.Butt;
            paint.BlendMode = SKBlendMode.Multiply;
        }

        canvas.DrawPath(path, paint);
    }

    /// <summary>Final image: crop applied, all annotations burned in.</summary>
    public static SKBitmap Flatten(SKBitmap source, AnnotationDocument document)
    {
        var crop = document.Crop is { IsEmpty: false } c
            ? c.Intersect(new ScreenRect(0, 0, source.Width, source.Height))
            : new ScreenRect(0, 0, source.Width, source.Height);
        if (crop.IsEmpty)
        {
            crop = new ScreenRect(0, 0, source.Width, source.Height);
        }

        var result = new SKBitmap(new SKImageInfo(crop.Width, crop.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(-crop.X, -crop.Y);
        Draw(canvas, source, document.Shapes);
        canvas.Flush();
        return result;
    }

    public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality = 92)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    /// <summary>Gallery preview, max. 480 px wide.</summary>
    public static byte[] Thumbnail(SKBitmap bitmap)
    {
        var width = Math.Min(480, bitmap.Width);
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * (width / (double)bitmap.Width)));
        using var small = bitmap.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium)
                          ?? bitmap.Copy();
        return Encode(small, SKEncodedImageFormat.Png);
    }

    public static SKBitmap ToBitmap(PixelImage image)
    {
        var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var target = bitmap.GetPixels();
        if (bitmap.RowBytes == image.Stride)
        {
            Marshal.Copy(image.Pixels, 0, target, image.Stride * image.Height);
        }
        else
        {
            for (var y = 0; y < image.Height; y++)
            {
                Marshal.Copy(image.Pixels, y * image.Stride, target + y * bitmap.RowBytes, image.Width * 4);
            }
        }

        return bitmap;
    }

    public static PixelImage ToPixelImage(SKBitmap bitmap)
    {
        using var bgra = bitmap.ColorType == SKColorType.Bgra8888 ? null : bitmap.Copy(SKColorType.Bgra8888);
        var source = bgra ?? bitmap;
        var stride = source.Width * 4;
        var pixels = new byte[stride * source.Height];
        var from = source.GetPixels();
        for (var y = 0; y < source.Height; y++)
        {
            Marshal.Copy(from + y * source.RowBytes, pixels, y * stride, stride);
        }

        return new PixelImage(source.Width, source.Height, pixels, stride);
    }

    /// <summary>Decodes PNG/JPG to BGRA (the format all drawing code expects).</summary>
    public static SKBitmap Decode(byte[] data)
    {
        var decoded = SKBitmap.Decode(data) ?? throw new InvalidDataException("Image could not be decoded.");
        if (decoded.ColorType == SKColorType.Bgra8888)
        {
            return decoded;
        }

        var converted = decoded.Copy(SKColorType.Bgra8888);
        decoded.Dispose();
        return converted;
    }

    // ---------------------------------------------------------------- shapes

    private static SKPaint Stroke(SKColor color, float width) => new()
    {
        Color = color,
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = width,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round
    };

    private static SKRect Rect(BoxShape b) => SKRect.Create(b.X, b.Y, b.Width, b.Height);

    private static void DrawArrow(SKCanvas canvas, ArrowShape a, SKColor color)
    {
        var dx = a.X2 - a.X1;
        var dy = a.Y2 - a.Y1;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1)
        {
            return;
        }

        var ux = dx / length;
        var uy = dy / length;
        var head = Math.Min(length * 0.6f, 6 + a.StrokeWidth * 3.2f);
        var halfWidth = head * 0.55f;

        // Shaft ends inside the head so the round cap does not poke out at the tip.
        var baseX = a.X2 - ux * head;
        var baseY = a.Y2 - uy * head;
        using (var paint = Stroke(color, a.StrokeWidth))
        {
            canvas.DrawLine(a.X1, a.Y1, baseX + ux * 1.5f, baseY + uy * 1.5f, paint);
        }

        using var path = new SKPath();
        path.MoveTo(a.X2, a.Y2);
        path.LineTo(baseX - uy * halfWidth, baseY + ux * halfWidth);
        path.LineTo(baseX + uy * halfWidth, baseY - ux * halfWidth);
        path.Close();
        using var fill = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawPath(path, fill);
    }

    /// <summary>Exact size of a text label (also used for selection and handles, see <see cref="TextShape.Measurer"/>).</summary>
    public static (float Width, float Height) MeasureText(TextShape t)
    {
        using var paint = new SKPaint { TextSize = t.FontSize, Typeface = TextFace };
        var lines = t.Text.Split('\n');
        var padding = t.FontSize * 0.3f;
        var width = lines.Max(l => paint.MeasureText(l));
        return (width + padding * 2, lines.Length * t.FontSize * 1.3f + padding);
    }

    private static void DrawText(SKCanvas canvas, TextShape t, SKColor color)
    {
        if (string.IsNullOrEmpty(t.Text))
        {
            return;
        }

        using var paint = new SKPaint { Color = color, IsAntialias = true, TextSize = t.FontSize, Typeface = TextFace };
        var lines = t.Text.Split('\n');
        var lineHeight = t.FontSize * 1.3f;
        var padding = t.FontSize * 0.3f;
        var (width, height) = MeasureText(t);
        var light = color.Red * 0.299 + color.Green * 0.587 + color.Blue * 0.114 > 150;

        SKColor? plate = t.EffectivePlate switch
        {
            TextPlate.Black => new SKColor(0x10, 0x10, 0x10, 235),
            TextPlate.White => new SKColor(255, 255, 255, 240),
            TextPlate.Custom => new SKColor(t.PlateColor),
            TextPlate.Auto => light ? new SKColor(0x1E, 0x1E, 0x1E, 230) : new SKColor(255, 255, 255, 235),
            _ => null
        };

        if (plate is { } p)
        {
            using var platePaint = new SKPaint { Color = p, IsAntialias = true };
            canvas.DrawRoundRect(SKRect.Create(t.X, t.Y, width, height), padding, padding, platePaint);
        }
        else
        {
            // Transparent: a soft contrasting shadow keeps the text readable on any background.
            paint.ImageFilter = SKImageFilter.CreateDropShadow(0, Math.Max(1, t.FontSize / 18), Math.Max(1.5f, t.FontSize / 12),
                Math.Max(1.5f, t.FontSize / 12), light ? new SKColor(0, 0, 0, 200) : new SKColor(255, 255, 255, 200));
        }

        for (var i = 0; i < lines.Length; i++)
        {
            canvas.DrawText(lines[i], t.X + padding, t.Y + padding * 0.5f + i * lineHeight + t.FontSize, paint);
        }
    }

    private static void DrawStep(SKCanvas canvas, StepShape s, SKColor color)
    {
        using var fill = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var ring = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2, s.Radius / 8) };
        canvas.DrawCircle(s.X, s.Y, s.Radius, fill);
        canvas.DrawCircle(s.X, s.Y, s.Radius, ring);

        var light = color.Red * 0.299 + color.Green * 0.587 + color.Blue * 0.114 > 150;
        using var text = new SKPaint
        {
            Color = light ? new SKColor(0x1E, 0x1E, 0x1E) : SKColors.White,
            IsAntialias = true,
            TextSize = s.Radius * 1.15f,
            Typeface = TextFace,
            TextAlign = SKTextAlign.Center
        };
        var number = s.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var bounds = new SKRect();
        text.MeasureText(number, ref bounds);
        canvas.DrawText(number, s.X, s.Y - bounds.MidY, text);
    }

    private static void Redact(SKCanvas canvas, SKBitmap source, RedactShape r)
    {
        var rect = Rect(r);
        switch (r.Mode)
        {
            case RedactMode.Bar:
                using (var bar = new SKPaint { Color = SKColors.Black, IsAntialias = true })
                {
                    canvas.DrawRect(rect, bar);
                }

                break;
            case RedactMode.Pixelate:
                Pixelate(canvas, source, SKRectI.Round(rect), (int)Math.Clamp(rect.Height / 3, 8, 24));
                break;
            default:
                SoftBlur(canvas, source, rect);
                break;
        }
    }

    /// <summary>
    /// Irreversible soft blur: the area is first shrunk to a few pixels (the text is gone, nothing can be "un-blurred"),
    /// then scaled up smoothly, blurred and drawn with feathered edges.
    /// </summary>
    private static void SoftBlur(SKCanvas canvas, SKBitmap source, SKRect rect)
    {
        var margin = Math.Max(6, rect.Height * 0.25f);
        var area = SKRectI.Intersect(SKRectI.Round(SKRect.Inflate(rect, margin, margin)), new SKRectI(0, 0, source.Width, source.Height));
        if (area.Width < 2 || area.Height < 2)
        {
            return;
        }

        using var subset = new SKBitmap();
        if (!source.ExtractSubset(subset, area))
        {
            return;
        }

        // About 1 pixel per 10 source pixels: details of letters cannot survive this.
        var tiny = new SKImageInfo(Math.Max(1, area.Width / 10), Math.Max(1, area.Height / 10), SKColorType.Bgra8888, SKAlphaType.Premul);
        using var small = subset.Resize(tiny, SKFilterQuality.Medium);
        if (small is null)
        {
            return;
        }

        using var smooth = small.Resize(new SKImageInfo(area.Width, area.Height, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
        if (smooth is null)
        {
            return;
        }

        var feather = Math.Max(2, rect.Height * 0.12f);
        using var shader = SKShader.CreateBitmap(smooth, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SKMatrix.CreateTranslation(area.Left, area.Top));
        using var paint = new SKPaint
        {
            Shader = shader,
            IsAntialias = true,
            ImageFilter = SKImageFilter.CreateBlur(feather, feather),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, feather)
        };
        // The feather lies OUTSIDE the box: inside, the blurred patch is fully opaque – no trace of the original.
        canvas.DrawRoundRect(SKRect.Inflate(rect, feather * 2, feather * 2), feather * 2, feather * 2, paint);
    }

    /// <summary>Block mosaic: scale the area down (averaging) and back up without smoothing.</summary>
    private static void Pixelate(SKCanvas canvas, SKBitmap source, SKRectI rect, int blockSize)
    {
        var area = SKRectI.Intersect(rect, new SKRectI(0, 0, source.Width, source.Height));
        if (area.Width < 2 || area.Height < 2)
        {
            return;
        }

        var block = Math.Max(4, blockSize);
        var smallWidth = Math.Max(1, area.Width / block);
        var smallHeight = Math.Max(1, area.Height / block);
        using var subset = new SKBitmap();
        if (!source.ExtractSubset(subset, area))
        {
            return;
        }

        using var small = subset.Resize(new SKImageInfo(smallWidth, smallHeight, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium);
        if (small is null)
        {
            return;
        }

        using var paint = new SKPaint { FilterQuality = SKFilterQuality.None, IsAntialias = false };
        canvas.DrawBitmap(small, SKRect.Create(area.Left, area.Top, area.Width, area.Height), paint);
    }
}
