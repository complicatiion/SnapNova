using System.IO.Compression;

namespace SnapNova.Core.Annotations;

/// <summary>
/// .snova = ZIP container: original.png (unchanged capture), document.json (annotations, crop, origin) and
/// thumbnail.png (gallery preview). Written to a temp file first, then swapped in (no half-written files).
/// </summary>
public static class SnovaFile
{
    public const string Extension = ".snova";
    private const string OriginalEntry = "original.png";
    private const string DocumentEntry = "document.json";
    private const string ThumbnailEntry = "thumbnail.png";

    public static void Save(string path, byte[] originalPng, AnnotationDocument document, byte[]? thumbnailPng)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            // PNG is already compressed: store it, compress only the JSON.
            Write(zip, OriginalEntry, originalPng, CompressionLevel.NoCompression);
            Write(zip, DocumentEntry, System.Text.Encoding.UTF8.GetBytes(document.ToJson()), CompressionLevel.Optimal);
            if (thumbnailPng is not null)
            {
                Write(zip, ThumbnailEntry, thumbnailPng, CompressionLevel.NoCompression);
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    public static (byte[] OriginalPng, AnnotationDocument Document) Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var original = Read(zip, OriginalEntry) ?? throw new InvalidDataException($"{Path.GetFileName(path)}: {OriginalEntry} missing.");
        var json = Read(zip, DocumentEntry);
        var document = json is null ? new AnnotationDocument() : AnnotationDocument.FromJson(System.Text.Encoding.UTF8.GetString(json));
        return (original, document);
    }

    /// <summary>Replaces only document.json (e.g. after the text recognition); original and thumbnail stay.</summary>
    public static void UpdateDocument(string path, AnnotationDocument document)
    {
        byte[] original;
        byte[]? thumbnail;
        using (var zip = ZipFile.OpenRead(path))
        {
            original = Read(zip, OriginalEntry) ?? throw new InvalidDataException($"{Path.GetFileName(path)}: {OriginalEntry} missing.");
            thumbnail = Read(zip, ThumbnailEntry);
        }

        Save(path, original, document, thumbnail);
    }

    /// <summary>Only document.json (fast: for the gallery search in recognized text).</summary>
    public static AnnotationDocument? ReadDocument(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var json = Read(zip, DocumentEntry);
            return json is null ? null : AnnotationDocument.FromJson(System.Text.Encoding.UTF8.GetString(json));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Preview for the gallery (null = no thumbnail stored or file unreadable).</summary>
    public static byte[]? ReadThumbnail(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return Read(zip, ThumbnailEntry) ?? Read(zip, OriginalEntry);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Write(ZipArchive zip, string name, byte[] data, CompressionLevel level)
    {
        var entry = zip.CreateEntry(name, level);
        using var stream = entry.Open();
        stream.Write(data);
    }

    private static byte[]? Read(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
