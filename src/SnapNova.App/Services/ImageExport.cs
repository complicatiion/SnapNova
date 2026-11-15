using SkiaSharp;

namespace SnapNova.App.Services;

/// <summary>Image formats of the export (all encoded by Skia, no extra package).</summary>
public static class ImageExport
{
    public static (SKEncodedImageFormat Format, string Extension) Format(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => (SKEncodedImageFormat.Jpeg, ".jpg"),
        ImageFormat.Webp => (SKEncodedImageFormat.Webp, ".webp"),
        _ => (SKEncodedImageFormat.Png, ".png")
    };

    public static ImageFormat FromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
        ".webp" => ImageFormat.Webp,
        _ => ImageFormat.Png
    };
}
