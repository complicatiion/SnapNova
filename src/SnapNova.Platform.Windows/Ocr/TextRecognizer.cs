using System.Runtime.InteropServices.WindowsRuntime;
using SnapNova.Core.Capture;
using SnapNova.Core.Ocr;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace SnapNova.Platform.Windows.Ocr;

/// <summary>
/// Text recognition with the OCR engine built into Windows (offline, uses the installed language packs).
/// Tall images (scrolling captures) are recognized in overlapping tiles, because the engine has a size limit.
/// </summary>
public static class TextRecognizer
{
    private const int Overlap = 96;

    public static bool IsAvailable => WinOcr.OcrEngine.AvailableRecognizerLanguages.Count > 0;

    public static async Task<OcrResult?> RecognizeAsync(PixelImage image, CancellationToken ct = default)
    {
        var engine = WinOcr.OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? WinOcr.OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"));
        if (engine is null)
        {
            return null;
        }

        var max = (int)WinOcr.OcrEngine.MaxImageDimension;

        // Very wide images: scale down to the limit (rare – 8K panoramas).
        var scale = image.Width > max ? (double)max / image.Width : 1.0;
        var source = scale < 1 ? Downscale(image, scale) : image;

        var lines = new List<OcrLine>();
        var tileHeight = Math.Min(max, source.Height);
        for (var top = 0; top < source.Height; top += tileHeight - Overlap)
        {
            ct.ThrowIfCancellationRequested();
            var height = Math.Min(tileHeight, source.Height - top);
            var tile = top == 0 && height == source.Height ? source : source.Crop(new ScreenRect(0, top, source.Width, height));
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(tile.Pixels.AsBuffer(0, tile.Stride * tile.Height), BitmapPixelFormat.Bgra8,
                tile.Width, tile.Height, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);

            foreach (var line in result.Lines)
            {
                var words = line.Words
                    .Select(w => new OcrWord(w.Text, (float)(w.BoundingRect.X / scale), (float)((w.BoundingRect.Y + top) / scale),
                        (float)(w.BoundingRect.Width / scale), (float)(w.BoundingRect.Height / scale)))
                    .ToList();
                if (words.Count == 0)
                {
                    continue;
                }

                // Lines in the overlap of the previous tile were already recognized there.
                var lineTop = words.Min(w => w.Y) * scale;
                if (top > 0 && lineTop < top + Overlap / 2.0)
                {
                    continue;
                }

                lines.Add(new OcrLine(words));
            }

            if (top + height >= source.Height)
            {
                break;
            }
        }

        return new OcrResult(lines, engine.RecognizerLanguage?.LanguageTag);
    }

    private static PixelImage Downscale(PixelImage image, double scale)
    {
        var width = Math.Max(1, (int)(image.Width * scale));
        var height = Math.Max(1, (int)(image.Height * scale));
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Min(image.Height - 1, (int)(y / scale));
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Min(image.Width - 1, (int)(x / scale));
                Buffer.BlockCopy(image.Pixels, sy * image.Stride + sx * 4, pixels, y * stride + x * 4, 4);
            }
        }

        return new PixelImage(width, height, pixels, stride);
    }
}
