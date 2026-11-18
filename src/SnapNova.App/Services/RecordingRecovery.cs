using System.Diagnostics;
using SnapNova.App.Localization;
using SnapNova.Core.Library;
using SnapNova.Core.Media;
using SnapNova.Platform.Windows.Recording;

namespace SnapNova.App.Services;

/// <summary>
/// After a crash or power loss, the parts of an unfinished recording stay in %LocalAppData%\SnapNova\temp\rec-*.
/// At start SnapNova offers to rescue them: the MKV parts are joined (picture copied, sound mixed) into the video folder.
/// No: the leftovers are deleted.
/// </summary>
public static class RecordingRecovery
{
    public static async Task CheckAsync(UiSettings settings, FfmpegGate gate, AppHost host, Action<string> saved)
    {
        var temp = AppEnvironment.TempDirectory;
        if (!Directory.Exists(temp))
        {
            return;
        }

        var leftovers = Directory.GetDirectories(temp, "rec-*")
            .Select(d => (Folder: d, Segments: Directory.GetFiles(d, "segment*.mkv").Where(f => new FileInfo(f).Length > 64 * 1024).Order().ToList()))
            .ToList();
        var rescuable = leftovers.Where(l => l.Segments.Count > 0).ToList();
        if (rescuable.Count == 0 || gate.Executable is not { } ffmpeg)
        {
            DeleteAll(leftovers.Select(l => l.Folder));
            return;
        }

        if (!await host.Ask(Loc.T("RecoverTitle"), Loc.F("RecoverText", rescuable.Count), Loc.T("RecoverYes")))
        {
            DeleteAll(leftovers.Select(l => l.Folder));
            return;
        }

        var folder = settings.EffectiveVideoFolder;
        Directory.CreateDirectory(folder);
        var rescued = 0;
        foreach (var (dir, segments) in rescuable)
        {
            var output = CaptureLibrary.NewFileName(folder, ".mkv", Directory.GetCreationTime(dir));
            output = Path.Combine(folder, Path.GetFileNameWithoutExtension(output) + "_recovered.mkv");
            var list = Path.Combine(dir, "recover.ffconcat");
            await File.WriteAllTextAsync(list, RecordingArguments.SegmentList(segments));
            var audio = Directory.GetFiles(dir, "*.wav").Where(f => new FileInfo(f).Length > 4096).ToList();
            if (await RunAsync(ffmpeg, RecordingArguments.Finish(list, audio, output))
                || (audio.Count > 0 && await RunAsync(ffmpeg, RecordingArguments.Finish(list, [], output))))
            {
                rescued++;
                saved(output);
            }
        }

        DeleteAll(leftovers.Select(l => l.Folder));
        host.ShowToast(Loc.F("RecoverDone", rescued));
    }

    private static async Task<bool> RunAsync(string ffmpeg, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
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

            JobObject.Shared.Add(process);
            _ = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void DeleteAll(IEnumerable<string> folders)
    {
        foreach (var folder in folders)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
