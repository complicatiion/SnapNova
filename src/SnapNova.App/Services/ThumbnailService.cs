using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using SnapNova.Core.Annotations;
using SnapNova.Core.Library;
using SnapNova.Core.Media;

namespace SnapNova.App.Services;

/// <summary>
/// Gallery previews: .snova → stored thumbnail, images → decoded at 320 px, videos → one frame via FFmpeg (only when
/// the user has FFmpeg; cached in %LocalAppData%\SnapNova\thumbs). Max. two decodes at a time.
/// </summary>
public sealed class ThumbnailService(FfmpegProvisioner ffmpeg)
{
    private const int Width = 320;
    private readonly SemaphoreSlim _gate = new(2);

    public async Task<Bitmap?> LoadAsync(LibraryItem item)
    {
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(async () => item.Kind switch
            {
                LibraryItemKind.Project => Decode(SnovaFile.ReadThumbnail(item.Path)),
                LibraryItemKind.Image => DecodeFile(item.Path),
                LibraryItemKind.Video => await VideoAsync(item),
                _ => null
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Bitmap? Decode(byte[]? data)
    {
        if (data is null)
        {
            return null;
        }

        using var stream = new MemoryStream(data);
        return Bitmap.DecodeToWidth(stream, Width, BitmapInterpolationMode.MediumQuality);
    }

    private static Bitmap? DecodeFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Bitmap.DecodeToWidth(stream, Width, BitmapInterpolationMode.MediumQuality);
    }

    private async Task<Bitmap?> VideoAsync(LibraryItem item)
    {
        var cache = CachePath(item);
        if (!File.Exists(cache))
        {
            if (ffmpeg.ResolveExecutable() is not { } exe)
            {
                return null;
            }

            Directory.CreateDirectory(AppEnvironment.ThumbnailDirectory);

            // Frame at 1 s; very short clips have none there – then the first frame.
            if (!await RunAsync(exe, RecordingArguments.Thumbnail(item.Path, cache)) || !File.Exists(cache))
            {
                await RunAsync(exe, RecordingArguments.ThumbnailFirstFrame(item.Path, cache));
            }
        }

        return File.Exists(cache) ? DecodeFile(cache) : null;
    }

    private static string CachePath(LibraryItem item)
    {
        var key = $"{item.Path}|{item.Size}|{item.Modified.Ticks}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(AppEnvironment.ThumbnailDirectory, hash + ".jpg");
    }

    private static async Task<bool> RunAsync(string exe, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            _ = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
