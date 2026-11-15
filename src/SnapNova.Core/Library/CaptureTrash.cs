using System.Text.Json;

namespace SnapNova.Core.Library;

/// <summary>A deleted capture in SnapNova's own recycle bin.</summary>
public sealed record TrashEntry(string TrashPath, string OriginalPath, DateTimeOffset Deleted, long Size)
{
    public string Name => Path.GetFileName(OriginalPath);
}

/// <summary>
/// SnapNova's own recycle bin: a hidden ".snapnova-trash" folder inside each capture folder. Moving stays on the same
/// drive (instant, also for large videos) and works on network shares, where the Windows recycle bin deletes for good.
/// The index (trash.json) keeps the original path and the time of deletion.
/// </summary>
public static class CaptureTrash
{
    public const string FolderName = ".snapnova-trash";
    private const string IndexName = "trash.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static string FolderOf(string captureFolder) => Path.Combine(captureFolder, FolderName);

    /// <summary>Moves the file into the recycle bin of its folder.</summary>
    public static TrashEntry MoveToTrash(string file)
    {
        lock (Gate)
        {
            var folder = FolderOf(Path.GetDirectoryName(Path.GetFullPath(file))!);
            var dir = Directory.CreateDirectory(folder);
            dir.Attributes |= FileAttributes.Hidden;
            var target = Path.Combine(folder, $"{DateTime.Now:yyyyMMddHHmmssfff}_{Path.GetFileName(file)}");
            var size = new FileInfo(file).Length;
            File.Move(file, target);
            var entry = new TrashEntry(target, Path.GetFullPath(file), DateTimeOffset.Now, size);
            var index = Load(folder);
            index.Add(entry);
            Save(folder, index);
            return entry;
        }
    }

    public static IReadOnlyList<TrashEntry> List(IEnumerable<string> captureFolders)
    {
        lock (Gate)
        {
            return captureFolders.Select(FolderOf).Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(f => Load(f).Where(e => File.Exists(e.TrashPath)))
                .OrderByDescending(e => e.Deleted)
                .ToList();
        }
    }

    /// <summary>Back to the original place (with "_restored" when a file of that name exists again).</summary>
    public static string Restore(TrashEntry entry)
    {
        lock (Gate)
        {
            var target = entry.OriginalPath;
            if (File.Exists(target))
            {
                target = Path.Combine(Path.GetDirectoryName(target)!,
                    $"{Path.GetFileNameWithoutExtension(target)}_restored{Path.GetExtension(target)}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(entry.TrashPath, target);
            Remove(entry);
            return target;
        }
    }

    public static void DeleteForever(TrashEntry entry)
    {
        lock (Gate)
        {
            if (File.Exists(entry.TrashPath))
            {
                File.Delete(entry.TrashPath);
            }

            Remove(entry);
        }
    }

    /// <summary>Deletes entries older than the retention (0 = keep forever). Returns the number removed.</summary>
    public static int Purge(IEnumerable<string> captureFolders, int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        var limit = DateTimeOffset.Now.AddDays(-retentionDays);
        var removed = 0;
        foreach (var entry in List(captureFolders).Where(e => e.Deleted < limit))
        {
            try
            {
                DeleteForever(entry);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private static void Remove(TrashEntry entry)
    {
        var folder = Path.GetDirectoryName(entry.TrashPath)!;
        var index = Load(folder);
        index.RemoveAll(e => string.Equals(e.TrashPath, entry.TrashPath, StringComparison.OrdinalIgnoreCase));
        Save(folder, index);
    }

    private static List<TrashEntry> Load(string folder)
    {
        try
        {
            var file = Path.Combine(folder, IndexName);
            return File.Exists(file) ? JsonSerializer.Deserialize<List<TrashEntry>>(File.ReadAllText(file), Json) ?? [] : [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Save(string folder, List<TrashEntry> index)
    {
        var file = Path.Combine(folder, IndexName);
        File.WriteAllText(file, JsonSerializer.Serialize(index, Json));
    }
}
