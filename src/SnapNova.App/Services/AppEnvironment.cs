using System.Diagnostics;
using System.Reflection;

namespace SnapNova.App.Services;

/// <summary>Paths and shell actions of the app.</summary>
public static class AppEnvironment
{
    public const string ProjectUrl = "https://github.com/complicatiion/SnapNova";
    public const string WebsiteUrl = "https://sksdesign.de";
    public const string MitLicenseUrl = "https://opensource.org/license/mit";

    private static readonly Lazy<bool> Portable = new(() => File.Exists(Path.Combine(AppContext.BaseDirectory, "SnapNova.portable")));

    public static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppEnvironment).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>Portable copy (marker file next to the exe): data in .\data, no autostart, no file associations.</summary>
    public static bool IsPortable => Portable.Value;

    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(BaseDirectory, "SnapNova.exe");

    public static string LicensesDirectory => Path.Combine(BaseDirectory, "licenses");

    /// <summary>Settings, thumbnails, FFmpeg, temporary recordings: %LocalAppData%\SnapNova (portable: .\data).</summary>
    public static string DataDirectory => IsPortable
        ? Path.Combine(BaseDirectory, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapNova");

    public static string ThumbnailDirectory => Path.Combine(DataDirectory, "thumbs");

    public static string TempDirectory => Path.Combine(DataDirectory, "temp");

    /// <summary>Default for images: Pictures\SnapNova.</summary>
    public static string DefaultImageFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnapNova");

    /// <summary>Default for videos: Videos\SnapNova.</summary>
    public static string DefaultVideoFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SnapNova");

    /// <summary>Default of the "one folder" mode: Documents\SnapNova (with Pictures and Videos inside).</summary>
    public static string DefaultCommonFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SnapNova");

    public const string UpdatesUrl = "https://github.com/complicatiion/SnapNova/releases";

    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No default browser.
        }
    }

    /// <summary>Opens a file with its default app (videos in the player of choice).</summary>
    public static void OpenFile(string? path) => OpenUrl(path is not null && File.Exists(path) ? path : null);

    /// <summary>Opens a folder, or selects a file in Explorer.</summary>
    public static void OpenFolder(string? path, bool create = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path) && OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
                return;
            }

            if (create)
            {
                Directory.CreateDirectory(path);
            }
            else if (!Directory.Exists(path))
            {
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Ignore.
        }
    }
}
