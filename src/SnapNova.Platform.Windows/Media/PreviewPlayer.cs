using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SnapNova.Core.Media;
using SnapNova.Platform.Windows.Recording;

namespace SnapNova.Platform.Windows.Media;

/// <summary>Duration and picture size of a video (read from FFmpeg's stream info).</summary>
public sealed record VideoInfo(TimeSpan Duration, int Width, int Height, double Fps, bool HasAudio);

/// <summary>
/// Preview player for the video editor, built on the user's ffmpeg.exe: one process decodes scaled BGRA frames, a second
/// one PCM audio (played with WASAPI). The picture is paced by a clock that starts with the sound. Seeking restarts both.
/// Good enough for previewing and trimming – not a replacement for a full media player.
/// </summary>
public sealed partial class PreviewPlayer(string ffmpeg) : IDisposable
{
    private CancellationTokenSource? _cts;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private Process? _video;
    private Process? _audio;
    private readonly Stopwatch _clock = new();
    private TimeSpan _start;
    private TaskCompletionSource _audioStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public VideoInfo? Info { get; private set; }

    public int FrameWidth { get; private set; }

    public int FrameHeight { get; private set; }

    public bool IsPlaying { get; private set; }

    public TimeSpan Position => IsPlaying ? _start + _clock.Elapsed : _start;

    /// <summary>A decoded frame (BGRA, FrameWidth × FrameHeight) – raised on a worker thread.</summary>
    public event Action<byte[]>? FrameReady;

    /// <summary>Playback reached the end.</summary>
    public event Action? Ended;

    public static async Task<VideoInfo?> ProbeAsync(string ffmpeg, string file)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        info.ArgumentList.Add("-hide_banner");
        info.ArgumentList.Add("-i");
        info.ArgumentList.Add(file);
        using var process = Process.Start(info);
        if (process is null)
        {
            return null;
        }

        var text = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var duration = DurationRegex().Match(text);
        var size = SizeRegex().Match(text);
        if (!duration.Success || !size.Success)
        {
            return null;
        }

        var fps = FpsRegex().Match(text);
        return new VideoInfo(
            TimeSpan.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture),
            fps.Success ? double.Parse(fps.Groups[1].Value, CultureInfo.InvariantCulture) : 30,
            text.Contains("Audio:", StringComparison.Ordinal));
    }

    /// <summary>Reads the video info and fits the preview into maxWidth × maxHeight (even sizes).</summary>
    public async Task<bool> OpenAsync(string file, int maxWidth, int maxHeight)
    {
        Info = await ProbeAsync(ffmpeg, file);
        if (Info is null)
        {
            return false;
        }

        File = file;
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / Info.Width, (double)maxHeight / Info.Height));
        FrameWidth = Math.Max(2, (int)(Info.Width * scale) & ~1);
        FrameHeight = Math.Max(2, (int)(Info.Height * scale) & ~1);
        return true;
    }

    public string? File { get; private set; }

    public void Play(TimeSpan from)
    {
        Stop();
        if (File is null || Info is null)
        {
            return;
        }

        _start = from;
        _clock.Reset();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var fps = Math.Clamp((int)Math.Round(Info.Fps), 10, 30);

        _video = Start(RecordingArguments.DecodeVideo(File, from, FrameWidth, FrameHeight, fps));
        _audio = Info.HasAudio ? Start(RecordingArguments.DecodeAudio(File, from)) : null;

        _buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 2)) { BufferDuration = TimeSpan.FromSeconds(4), DiscardOnBufferOverflow = false };
        try
        {
            _output = new WasapiOut(AudioClientShareMode.Shared, 120);
            _output.Init(_buffer);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _output = null; // no sound device: picture only
        }

        IsPlaying = true;
        _audioStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_audio is null || _output is null)
        {
            _audioStarted.TrySetResult();
        }

        _ = Task.Run(() => PumpAudio(_audio, ct), ct);
        _ = Task.Run(() => PumpVideo(_video, fps, ct), ct);
    }

    private async Task PumpAudio(Process? process, CancellationToken ct)
    {
        if (process is null || _buffer is null)
        {
            return;
        }

        var stream = process.StandardOutput.BaseStream;
        var chunk = new byte[19200];
        var started = false;
        try
        {
            int read;
            while (!ct.IsCancellationRequested && (read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                while (_buffer.BufferedDuration > TimeSpan.FromSeconds(2) && !ct.IsCancellationRequested)
                {
                    await Task.Delay(50, ct);
                }

                _buffer.AddSamples(chunk, 0, read);
                if (!started)
                {
                    started = true;
                    _output?.Play();
                    _audioStarted.TrySetResult();
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private async Task PumpVideo(Process? process, int fps, CancellationToken ct)
    {
        if (process is null)
        {
            return;
        }

        var stream = process.StandardOutput.BaseStream;
        var frameSize = FrameWidth * FrameHeight * 4;
        var index = 0;

        // The picture clock starts with the sound (at most 1.5 s wait), so both run in step.
        await Task.WhenAny(_audioStarted.Task, Task.Delay(1500, ct));
        _clock.Restart();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = new byte[frameSize];
                var filled = 0;
                while (filled < frameSize)
                {
                    var read = await stream.ReadAsync(frame.AsMemory(filled), ct);
                    if (read == 0)
                    {
                        IsPlaying = false;
                        Ended?.Invoke();
                        return;
                    }

                    filled += read;
                }

                var due = TimeSpan.FromSeconds((double)index / fps);
                var wait = due - _clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct);
                }

                index++;
                FrameReady?.Invoke(frame);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>One still frame at a position (for seeking while paused).</summary>
    public async Task ShowFrameAsync(TimeSpan at)
    {
        Stop();
        if (File is null)
        {
            return;
        }

        _start = at;
        var args = RecordingArguments.DecodeVideo(File, at, FrameWidth, FrameHeight, 1).ToList();
        args.InsertRange(args.Count - 1, ["-frames:v", "1"]);
        using var process = Start(args);
        if (process is null)
        {
            return;
        }

        var frame = new byte[FrameWidth * FrameHeight * 4];
        var filled = 0;
        var stream = process.StandardOutput.BaseStream;
        int read;
        while (filled < frame.Length && (read = await stream.ReadAsync(frame.AsMemory(filled))) > 0)
        {
            filled += read;
        }

        if (filled == frame.Length)
        {
            FrameReady?.Invoke(frame);
        }
    }

    public void Pause()
    {
        var position = Position;
        Stop();
        _start = position;
    }

    private void Stop()
    {
        IsPlaying = false;
        _clock.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        Kill(_video);
        Kill(_audio);
        _video = null;
        _audio = null;
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _buffer = null;
    }

    private Process? Start(IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(ffmpeg)
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

        var process = Process.Start(info);
        if (process is not null)
        {
            JobObject.Shared.Add(process);
            _ = process.StandardError.ReadToEndAsync();
        }

        return process;
    }

    private static void Kill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        process.Dispose();
    }

    public void Dispose() => Stop();

    [GeneratedRegex(@"Duration: (\d+:\d{2}:\d{2}(?:\.\d+)?)")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"Video: .*?, (\d{2,5})x(\d{2,5})")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"(\d+(?:\.\d+)?) fps")]
    private static partial Regex FpsRegex();
}
