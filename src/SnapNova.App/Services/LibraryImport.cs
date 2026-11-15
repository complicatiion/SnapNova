using SnapNova.App.Rendering;
using SnapNova.Core.Annotations;
using SnapNova.Core.Capture;
using SnapNova.Core.Library;
using SnapNova.Platform.Windows.Shell;

namespace SnapNova.App.Services;

/// <summary>Ctrl+V in the gallery: files copied in Explorer or an image on the clipboard become captures.</summary>
public static class LibraryImport
{
    public static int FromClipboard(UiSettings settings)
    {
        var count = 0;
        foreach (var file in ClipboardImage.GetFiles())
        {
            if (CaptureLibrary.KindOf(file) is not { } kind || !File.Exists(file))
            {
                continue;
            }

            var folder = kind == LibraryItemKind.Video ? settings.EffectiveVideoFolder : settings.EffectiveImageFolder;
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, Path.GetFileName(file));
            for (var i = 2; File.Exists(target); i++)
            {
                target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(file)}_{i}{Path.GetExtension(file)}");
            }

            // Already in the library folder: nothing to import.
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, target);
            count++;
        }

        if (count > 0)
        {
            return count;
        }

        var (png, image) = ClipboardImage.GetImage();
        if (png is null && image is null)
        {
            return 0;
        }

        using var bitmap = png is not null ? AnnotationRenderer.Decode(png) : AnnotationRenderer.ToBitmap(image!);
        var original = png ?? AnnotationRenderer.Encode(bitmap, SkiaSharp.SKEncodedImageFormat.Png);
        var imageFolder = settings.EffectiveImageFolder;
        Directory.CreateDirectory(imageFolder);
        var path = CaptureLibrary.NewFileName(imageFolder, SnovaFile.Extension, DateTime.Now);
        var info = new CaptureInfo(DateTimeOffset.Now, "Clipboard", null, new ScreenRect(0, 0, bitmap.Width, bitmap.Height));
        SnovaFile.Save(path, original, new AnnotationDocument { Info = info }, AnnotationRenderer.Thumbnail(bitmap));
        return 1;
    }
}
