using System.Diagnostics;

namespace SnapNova.Core.Media;

/// <summary>
/// Runs ffmpeg.exe to read its version and to find the video encoder that really works on this PC.
/// "-encoders" alone is not enough: h264_nvenc is listed in every build, also without an NVIDIA GPU – so each candidate
/// encodes a few test frames.
/// </summary>
public static class FfmpegProbe
{
    /// <summary>Order of preference: GPU encoders, then Media Foundation (Windows, also hardware where available), then x264 (GPL build).</summary>
    public static readonly string[] H264Candidates = ["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf", "libx264"];

    public static async Task<string?> VersionAsync(string executable, CancellationToken ct)
    {
        var (exit, output) = await RunAsync(executable, ["-hide_banner", "-version"], TimeSpan.FromSeconds(10), ct);
        if (exit != 0)
        {
            return null;
        }

        // "ffmpeg version n8.1-12-gabc123-20261101 Copyright (c) ..."
        var first = output.Split('\n', 2)[0];
        var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[0] == "ffmpeg" && parts[1] == "version" ? parts[2] : null;
    }

    /// <summary>True when the build has the ddagrab source (Desktop Duplication on the GPU, FFmpeg 6.1+).</summary>
    public static async Task<bool> HasDdagrabAsync(string executable, CancellationToken ct)
    {
        var (exit, output) = await RunAsync(executable, ["-hide_banner", "-filters"], TimeSpan.FromSeconds(10), ct);
        return exit == 0 && output.Contains(" ddagrab ", StringComparison.Ordinal);
    }

    /// <summary>First encoder that encodes test frames successfully; null = none (should not happen with h264_mf).</summary>
    public static async Task<string?> BestH264EncoderAsync(string executable, CancellationToken ct)
    {
        foreach (var encoder in H264Candidates)
        {
            // 0.2 s of 256×256 black at 30 fps, thrown away. Hardware encoders need even sizes, 256 is safe everywhere.
            var (exit, _) = await RunAsync(executable,
                ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=256x256:r=30:d=0.2",
                 "-c:v", encoder, "-f", "null", "-"],
                TimeSpan.FromSeconds(15), ct);
            if (exit == 0)
            {
                return encoder;
            }
        }

        return null;
    }

    private static async Task<(int Exit, string Output)> RunAsync(string exe, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, "");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return (process.ExitCode, await stdout + await stderr);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            ct.ThrowIfCancellationRequested();
            return (-1, "");
        }
    }
}
