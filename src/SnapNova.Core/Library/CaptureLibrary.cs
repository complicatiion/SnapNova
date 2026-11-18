using SnapNova.Core.Annotations;

namespace SnapNova.Core.Library;

public enum LibraryItemKind
{
    /// <summary>.snova: capture with editable annotations.</summary>
    Project,
    Image,
    Video
}

public sealed record LibraryItem(string Path, LibraryItemKind Kind, DateTime Modified, long Size)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();

    public bool IsVideo => Kind == LibraryItemKind.Video;
}

/// <summary>
/// The library is a plain folder (default: Pictures\SnapNova). Files are the truth – no database that can get out of sync.
/// An image next to a .snova with the same name is its export and is not listed twice.
/// </summary>
public static class CaptureLibrary
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp"];
    public static readonly string[] VideoExtensions = [".mp4", ".mkv", ".webm"];

    public static LibraryItemKind? KindOf(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext == SnovaFile.Extension ? LibraryItemKind.Project
            : ImageExtensions.Contains(ext) ? LibraryItemKind.Image
            : VideoExtensions.Contains(ext) ? LibraryItemKind.Video
            : null;
    }

    /// <summary>All captures of several folders (image and video folder), newest first, each file once.</summary>
    public static IReadOnlyList<LibraryItem> Scan(IEnumerable<string> folders) =>
        folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(Scan).OrderByDescending(i => i.Modified).ToList();

    public static IReadOnlyList<LibraryItem> Scan(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var files = new List<FileInfo>();
        try
        {
            files.AddRange(new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.TopDirectoryOnly));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var projects = files.Where(f => f.Extension.Equals(SnovaFile.Extension, StringComparison.OrdinalIgnoreCase))
            .Select(f => System.IO.Path.GetFileNameWithoutExtension(f.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = new List<LibraryItem>();
        foreach (var file in files)
        {
            if (KindOf(file.FullName) is not { } kind)
            {
                continue;
            }

            if (kind == LibraryItemKind.Image && projects.Contains(System.IO.Path.GetFileNameWithoutExtension(file.Name)))
            {
                continue;
            }

            items.Add(new LibraryItem(file.FullName, kind, file.LastWriteTime, file.Length));
        }

        return items.OrderByDescending(i => i.Modified).ToList();
    }

    /// <summary>SnapNova_2026-11-14_20-15-03.snova – with _2, _3 … when the second already exists.</summary>
    /// <summary>Marker of further recordings of a multi-recording: "SnapNova_…_rec2" belongs to "SnapNova_…".</summary>
    public const string PartMarker = "_rec";

    private static readonly System.Text.RegularExpressions.Regex PartPattern =
        new(@"^(?<stem>.+)_rec(?<n>[2-9])(-\d+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>File of recording <paramref name="number"/> (2…) of a multi-recording: same name as the main recording
    /// plus "_rec{n}", so the gallery shows the parts together.</summary>
    public static string PartFileName(string folder, string extension, string? primaryOutput, int number)
    {
        var stem = primaryOutput is null
            ? System.IO.Path.GetFileNameWithoutExtension(NewFileName(folder, extension, DateTime.Now))
            : System.IO.Path.GetFileNameWithoutExtension(primaryOutput);
        var path = System.IO.Path.Combine(folder, $"{stem}{PartMarker}{number}{extension}");
        for (var i = 2; File.Exists(path); i++)
        {
            path = System.IO.Path.Combine(folder, $"{stem}{PartMarker}{number}-{i}{extension}");
        }

        return path;
    }

    /// <summary>For "SnapNova_…_rec3": ("SnapNova_…", 3); for any other name null.</summary>
    public static (string Stem, int Number)? PartOf(string name)
    {
        var match = PartPattern.Match(name);
        return match.Success ? (match.Groups["stem"].Value, int.Parse(match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)) : null;
    }

    public static string NewFileName(string folder, string extension, DateTime timestamp)
    {
        var stem = "SnapNova_" + timestamp.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        var path = System.IO.Path.Combine(folder, stem + extension);
        for (var i = 2; File.Exists(path) || File.Exists(System.IO.Path.ChangeExtension(path, SnovaFile.Extension)); i++)
        {
            path = System.IO.Path.Combine(folder, $"{stem}_{i}{extension}");
        }

        return path;
    }
}
