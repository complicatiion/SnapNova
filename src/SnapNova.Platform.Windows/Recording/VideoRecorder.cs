using System.Diagnostics;
using System.Text;
using SnapNova.Core.Capture;
using SnapNova.Core.Media;
using SnapNova.Platform.Windows.Input;

namespace SnapNova.Platform.Windows.Recording;

/// <summary>FFmpeg did not start recording (wrong encoder, ddagrab not available in this session …). Log = last FFmpeg lines.</summary>
public sealed class RecordingStartException(string message, string log) : Exception(message)
{
    public string Log { get; } = log;
}

/// <summary>
/// One screen recording. FFmpeg runs as a separate process per segment (pause = end the segment, resume = new segment),
/// audio runs in-process. Stop joins the segments, mixes the audio and writes the final MP4.
///
/// Sync: audio is written only while a segment is really running – from FFmpeg's "Press [q]" line (output opened, first
/// frames follow) until the pause/stop request.
/// </summary>
public sealed partial class VideoRecorder : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

    private readonly string _ffmpeg;
    private readonly RecordingOptions _options;
    private readonly string _workDir;
    private readonly AudioRecorder? _audio;
    private readonly List<string> _segments = [];
    private readonly Stopwatch _elapsed = new();
    private readonly StringBuilder _log = new();
    private readonly ScreenRect _area;
    private readonly CursorTrack _track;
    private readonly CancellationTokenSource _sampling = new();
    private Process? _process;
    private bool _stopRequested;

    /// <param name="options">OutputFile is ignored – segments go to the work folder (MKV: stays playable if the PC crashes).</param>
    /// <param name="area">Recorded area in virtual-desktop pixels (for the mouse track).</param>
    /// <param name="processId">Above 0: record only the sound of this program (additional recordings).</param>
    public VideoRecorder(string ffmpeg, RecordingOptions options, string workDir, bool systemSound, bool microphone, ScreenRect area,
        int processId = 0)
    {
        _ffmpeg = ffmpeg;
        _options = options;
        _workDir = workDir;
        _area = area;
        _track = new CursorTrack { Width = area.Width, Height = area.Height };
        Directory.CreateDirectory(workDir);
        if (systemSound || microphone || processId > 0)
        {
            _audio = new AudioRecorder(workDir, systemSound, microphone, processId);
        }
    }

    /// <summary>FFmpeg ended on its own while recording (disk full, display mode change …). Raised on a worker thread.</summary>
    public event Action<string>? Failed;

    public TimeSpan Elapsed => _elapsed.Elapsed;

    private int _droppedBase;
    private int _segmentDrops;

    /// <summary>Frames FFmpeg had to drop so far (PC too slow for size/fps/encoder) – shown as a warning.</summary>
    public int DroppedFrames { get; private set; }

    public event Action<int>? DroppedFramesChanged;

    [System.Text.RegularExpressions.GeneratedRegex(@"drop=\s*(\d+)")]
    private static partial System.Text.RegularExpressions.Regex DropRegex();

    public bool IsPaused { get; private set; }

    public IReadOnlyList<string> AudioWarnings => _audio?.Warnings ?? [];

    public string Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToString();
            }
        }
    }

    public async Task StartAsync()
    {
        _audio?.Start();
        await StartSegmentAsync();
        _ = SampleCursorAsync(_sampling.Token);
    }

    /// <summary>Mouse track at 30 Hz while recording (paused time excluded) – for "Follow the mouse" in the video editor.</summary>
    private async Task SampleCursorAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (IsPaused || _process is null)
                {
                    continue;
                }

                var (x, y) = Capture.ScreenCapture.CursorPosition();
                var down = MouseInput.IsDown(MouseInput.VkLeftButton);
                lock (_track)
                {
                    _track.Samples.Add(new CursorSample(_elapsed.Elapsed.TotalSeconds, x - _area.X, y - _area.Y, down));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task PauseAsync()
    {
        if (IsPaused)
        {
            return;
        }

        IsPaused = true;
        await EndSegmentAsync();
    }

    public async Task ResumeAsync()
    {
        if (!IsPaused)
        {
            return;
        }

        await StartSegmentAsync();
        IsPaused = false;
    }

    /// <summary>Ends the recording and writes <paramref name="output"/>. The work folder is removed afterwards.</summary>
    public async Task StopAsync(string output)
    {
        _stopRequested = true;
        _sampling.Cancel();
        await EndSegmentAsync();
        _audio?.Stop();
        try
        {
            lock (_track)
            {
                _track.Save(CursorTrack.PathFor(output));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        var segments = _segments.Where(s => File.Exists(s) && new FileInfo(s).Length > 0).ToList();
        if (segments.Count == 0)
        {
            throw new RecordingStartException("No video was recorded.", Log);
        }

        var audio = _audio?.Files ?? [];
        if (segments.Count == 1 && audio.Count == 0 && string.Equals(Path.GetExtension(output), Path.GetExtension(segments[0]), StringComparison.OrdinalIgnoreCase))
        {
            File.Move(segments[0], output, overwrite: true);
            return;
        }

        var list = Path.Combine(_workDir, "segments.txt");
        await File.WriteAllTextAsync(list, RecordingArguments.SegmentList(segments));
        var (exit, log) = await RunAsync(RecordingArguments.Finish(list, audio, output), TimeSpan.FromMinutes(10));
        if (exit != 0 || !File.Exists(output))
        {
            // Keep at least the picture: the first segment as it is (MKV next to the intended file).
            File.Copy(segments[0], Path.ChangeExtension(output, ".mkv"), overwrite: true);
            throw new RecordingStartException("Joining the recording failed – the video was saved without sound.", log);
        }
    }

    /// <summary>Discards the recording.</summary>
    public async Task CancelAsync()
    {
        _stopRequested = true;
        _sampling.Cancel();
        await EndSegmentAsync();
        _audio?.Stop();
    }

    private async Task StartSegmentAsync()
    {
        _stopRequested = false;
        var file = Path.Combine(_workDir, $"segment{_segments.Count + 1:00}.mkv");
        _droppedBase += _segmentDrops;
        _segmentDrops = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var info = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in RecordingArguments.Record(_options with { OutputFile = file }))
        {
            info.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            // Progress lines (every 2 s): only the dropped-frame counter matters, they do not go into the log.
            if (e.Data.StartsWith("frame=", StringComparison.Ordinal))
            {
                var drop = DropRegex().Match(e.Data);
                if (drop.Success && int.TryParse(drop.Groups[1].Value, out var dropped) && _droppedBase + dropped != DroppedFrames)
                {
                    _segmentDrops = dropped;
                    DroppedFrames = _droppedBase + dropped;
                    DroppedFramesChanged?.Invoke(DroppedFrames);
                }

                return;
            }

            AppendLog(e.Data);

            // Printed when the output is open and encoding begins.
            if (e.Data.Contains("Press [q]", StringComparison.Ordinal))
            {
                started.TrySetResult();
            }
        };
        process.OutputDataReceived += (_, _) => { };
        process.Exited += (_, _) =>
        {
            started.TrySetException(new RecordingStartException("FFmpeg ended before recording started.", Log));
            if (!_stopRequested && ReferenceEquals(_process, process) && !IsPaused)
            {
                Failed?.Invoke(Log);
            }
        };

        process.Start();
        JobObject.Shared.Add(process);
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        _process = process;

        var finished = await Task.WhenAny(started.Task, Task.Delay(StartTimeout));
        if (finished != started.Task)
        {
            Kill(process);
            throw new RecordingStartException("FFmpeg did not start recording in time.", Log);
        }

        await started.Task; // rethrows the start failure
        if (_audio is not null)
        {
            _audio.Paused = false;
        }

        _segments.Add(file);
        _elapsed.Start();
    }

    private async Task EndSegmentAsync()
    {
        if (_audio is not null)
        {
            _audio.Paused = true;
        }

        _elapsed.Stop();
        var process = _process;
        if (process is null)
        {
            return;
        }

        _process = null;
        if (!process.HasExited)
        {
            try
            {
                // "q" = finish the file properly (moov atom written, playable).
                await process.StandardInput.WriteAsync('q');
                await process.StandardInput.FlushAsync();
            }
            catch (IOException)
            {
            }

            using var timeout = new CancellationTokenSource(StopTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
            }
        }

        process.Dispose();
    }

    private async Task<(int Exit, string Log)> RunAsync(IReadOnlyList<string> args, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(_ffmpeg)
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

        using var process = Process.Start(info)!;
        JobObject.Shared.Add(process);
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return (-1, "timeout");
        }

        var log = await stderr;
        AppendLog(log);
        return (process.ExitCode, log);
    }

    private void AppendLog(string line)
    {
        lock (_log)
        {
            // Keep the end: that is where FFmpeg explains what went wrong.
            if (_log.Length > 32_000)
            {
                _log.Remove(0, 16_000);
            }

            _log.AppendLine(line);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            await CancelAsync();
        }

        _sampling.Cancel();
        _sampling.Dispose();
        _audio?.Dispose();
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
