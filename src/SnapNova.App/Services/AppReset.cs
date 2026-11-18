using System.Diagnostics;
using SnapNova.Core.Annotations;
using SnapNova.Core.Library;

namespace SnapNova.App.Services;

/// <summary>
/// "Reset SnapNova": settings, toolbar position, caches and temporary files go; the setup defaults apply again at the next
/// start (autostart, tray, animations). FFmpeg stays unless asked. Captures are deleted only on explicit request – and then
/// only files that clearly belong to SnapNova, never whole folders (a capture folder may be the desktop or "Documents").
/// </summary>
public static class AppReset
{
    private static readonly string[] MediaExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".mp4", ".mkv", ".webm"];

    /// <summary>Files of SnapNova in the capture folders: every .snova, files named SnapNova_* (captures, exports,
    /// mouse tracks) and the recycle bin folder.</summary>
    public static IReadOnlyList<string> MediaFiles(UiSettings settings)
    {
        var files = new List<string>();
        foreach (var folder in settings.LibraryFolders.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                var ext = Path.GetExtension(file).ToLowerInvariant();
                var ours = name.StartsWith("SnapNova_", StringComparison.OrdinalIgnoreCase);
                if (ext == SnovaFile.Extension || (ours && (MediaExtensions.Contains(ext) || name.EndsWith(".cursor.json", StringComparison.OrdinalIgnoreCase))))
                {
                    files.Add(file);
                }
            }

            var trash = Path.Combine(folder, CaptureTrash.FolderName);
            if (Directory.Exists(trash))
            {
                files.AddRange(Directory.EnumerateFiles(trash, "*", SearchOption.AllDirectories));
            }
        }

        return files;
    }

    public static (int Count, long Bytes) MediaSummary(UiSettings settings)
    {
        var files = MediaFiles(settings);
        long bytes = 0;
        foreach (var file in files)
        {
            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch (IOException)
            {
            }
        }

        return (files.Count(f => !Path.GetFileName(f).Equals("trash.json", StringComparison.OrdinalIgnoreCase)), bytes);
    }

    /// <summary>Does the reset. The caller restarts SnapNova afterwards.</summary>
    public static void Run(UiSettings settings, bool deleteMedia, bool removeFfmpeg)
    {
        settings.Frozen = true;
        if (deleteMedia)
        {
            var folders = settings.LibraryFolders.ToList();
            foreach (var file in MediaFiles(settings))
            {
                TryDelete(file);
            }

            foreach (var folder in folders)
            {
                TryDeleteDirectory(Path.Combine(folder, CaptureTrash.FolderName));

                // Only SnapNova's own default folders are removed, and only when they are empty now.
                if (IsDefaultFolder(folder) && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    TryDeleteDirectory(folder);
                }
            }

            if (Directory.Exists(AppEnvironment.DefaultCommonFolder) && !Directory.EnumerateFileSystemEntries(AppEnvironment.DefaultCommonFolder).Any())
            {
                TryDeleteDirectory(AppEnvironment.DefaultCommonFolder);
            }
        }

        // Data folder: everything except FFmpeg (tools) unless it should go as well.
        var data = AppEnvironment.DataDirectory;
        if (Directory.Exists(data))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(data).ToList())
            {
                if (!removeFfmpeg && string.Equals(Path.GetFileName(entry), "tools", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    TryDeleteDirectory(entry);
                }
                else
                {
                    TryDelete(entry);
                }
            }
        }

        // Autostart: the per-user entry goes; the setup defaults set it again at the next start.
        if (OperatingSystem.IsWindows())
        {
            SnapNova.Platform.Windows.Startup.Autostart.Disable();
        }
    }

    /// <summary>Starts a new SnapNova that waits until this one has ended.</summary>
    public static void Restart()
    {
        var info = new ProcessStartInfo(AppEnvironment.ExecutablePath) { UseShellExecute = false };
        info.ArgumentList.Add(Program.RestartArgument);
        Process.Start(info)?.Dispose();
    }

    private static bool IsDefaultFolder(string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        return new[] { AppEnvironment.DefaultImageFolder, AppEnvironment.DefaultVideoFolder, AppEnvironment.DefaultCommonFolder }
            .Any(d => full.StartsWith(Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
