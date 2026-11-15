using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SnapNova.App.Localization;
using SnapNova.App.Services;
using SnapNova.Core.Capture;
using SnapNova.Core.Library;
using SnapNova.Core.Media;
using SnapNova.Platform.Windows.Media;
using SnapNova.Platform.Windows.Recording;

namespace SnapNova.App.ViewModels;

public enum VideoExportKind
{
    Trim,
    Gif,
    WebM,
    Follow
}

/// <summary>
/// Video editor and SnapNova player: plays at once (loop on by default), cuts with several cut points (split, keep or
/// remove each part), exports the kept parts as video (fast or frame-exact), GIF, WebM or "follow the mouse" zoom.
/// </summary>
public sealed partial class VideoEditorViewModel : ObservableObject, IDisposable
{
    private readonly string _ffmpeg;
    private readonly UiSettings _settings;
    private readonly CaptureCoordinator _capture;
    private readonly CursorTrack? _track;

    public VideoEditorViewModel(string file, string ffmpeg, UiSettings settings, CaptureCoordinator capture)
    {
        File = file;
        _ffmpeg = ffmpeg;
        _settings = settings;
        _capture = capture;
        Player = new PreviewPlayer(ffmpeg);
        Player.Ended += () => Dispatcher.UIThread.Post(OnEnded);
        _track = CursorTrack.Load(CursorTrack.PathFor(file));
        _followZoom = settings.FollowZoom;
        _loop = settings.VideoLoop;
        Edit = new TrimEdit(1);
    }

    public string File { get; }

    public string Title => "SnapNova – " + Path.GetFileName(File);

    public PreviewPlayer Player { get; }

    /// <summary>The cut: trim start/end and cut-out parts.</summary>
    public TrimEdit Edit { get; }

    public bool CanFollow => _track is { Samples.Count: > 0 };

    public IReadOnlyList<Choice<int>> GifWidths { get; } = [new(480, "480 px"), new(640, "640 px"), new(800, "800 px"), new(1024, "1024 px")];

    [ObservableProperty] private double _duration = 1;
    [ObservableProperty] private double _position;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _exactCut;
    [ObservableProperty] private bool _loop;
    [ObservableProperty] private double _followZoom;
    [ObservableProperty] private Choice<int>? _gifWidth;
    [ObservableProperty] private string _status = "";

    /// <summary>Selected cut-out part in the timeline (-1 = none) – Del brings it back.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCutCommand))]
    private int _selectedRemoved = -1;

    public bool HasSelection => Edit.Selection is not null;

    public bool HasRemovedSelected => SelectedRemoved >= 0 && SelectedRemoved < Edit.Removed.Count;

    public string PositionText => $"{Format(Position)} / {Format(Duration)}";

    /// <summary>Status pill bottom right: "uncut" or how much remains.</summary>
    public string CutText => Edit.IsUncut ? Loc.T("VedUncut") : Loc.F("VedCutInfo", Edit.Kept.Count, Format(Edit.KeptLength));

    public bool IsCut => !Edit.IsUncut;

    /// <summary>Timeline redraw (cut or playhead changed).</summary>
    public event Action? TimelineChanged;

    /// <summary>Set by the window: asks for the target file (suggested name, extension).</summary>
    public Func<string, string, Task<string?>>? PickTarget { get; set; }

    /// <summary>Raised after a successful export (gallery refresh).</summary>
    public event Action<string>? Exported;

    private (int Width, int Height) _previewSize = (1280, 720);

    public async Task<bool> OpenAsync(int maxWidth, int maxHeight)
    {
        _previewSize = (maxWidth, maxHeight);
        if (!await Player.OpenAsync(File, maxWidth, maxHeight) || Player.Info is null)
        {
            return false;
        }

        Duration = Math.Max(0.1, Player.Info.Duration.TotalSeconds);
        Edit.Reset(Duration);
        SelectedRemoved = -1;
        GifWidth = GifWidths[1];
        RaiseCut();

        // The player starts right away (double-click on a video = watch it).
        PlayPause();
        return true;
    }

    partial void OnPositionChanged(double value)
    {
        OnPropertyChanged(nameof(PositionText));
        TimelineChanged?.Invoke();
    }

    partial void OnLoopChanged(bool value)
    {
        _settings.VideoLoop = value;
        _settings.Save();
    }

    /// <summary>The cut changed (buttons, keys or the timeline): texts, commands, redraw.</summary>
    public void RaiseCut()
    {
        if (SelectedRemoved >= Edit.Removed.Count)
        {
            SelectedRemoved = -1;
        }

        OnPropertyChanged(nameof(CutText));
        OnPropertyChanged(nameof(IsCut));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasRemovedSelected));
        CutOutCommand.NotifyCanExecuteChanged();
        RestoreCutCommand.NotifyCanExecuteChanged();
        TimelineChanged?.Invoke();
    }

    /// <summary>Called by the window's timer while playing: skips removed parts, loops or stops at the end.</summary>
    public void Tick()
    {
        if (!Player.IsPlaying)
        {
            return;
        }

        Position = Math.Min(Duration, Player.Position.TotalSeconds);
        if (!Edit.IsKept(Position))
        {
            if (Edit.NextKept(Position) is { } next)
            {
                Position = next;
                Player.Play(TimeSpan.FromSeconds(next));
            }
            else
            {
                OnEnded();
            }
        }
    }

    private void OnEnded()
    {
        if (Loop && Edit.Kept.Count > 0)
        {
            Position = Edit.FirstKept;
            Player.Play(TimeSpan.FromSeconds(Position));
            IsPlaying = true;
        }
        else
        {
            Player.Pause();
            IsPlaying = false;
        }
    }

    /// <summary>The user clicked or dragged on the timeline.</summary>
    public async Task SeekAsync(double seconds, bool final)
    {
        Position = Math.Clamp(seconds, 0, Duration);
        if (!final)
        {
            return;
        }

        if (IsPlaying)
        {
            Player.Play(TimeSpan.FromSeconds(Position));
        }
        else
        {
            await Player.ShowFrameAsync(TimeSpan.FromSeconds(Position));
        }
    }

    public void SelectRemoved(int index)
    {
        SelectedRemoved = index;
        OnPropertyChanged(nameof(HasRemovedSelected));
        TimelineChanged?.Invoke();
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (IsPlaying)
        {
            Player.Pause();
            IsPlaying = false;
            return;
        }

        if (Position >= Duration - 0.05)
        {
            Position = Edit.FirstKept;
        }

        if (Edit.NextKept(Position) is { } start)
        {
            Position = start;
        }

        Player.Play(TimeSpan.FromSeconds(Position));
        IsPlaying = true;
    }

    // ---------------------------------------------------------------- cutting
    // Trim: I / O set start and end at the playhead (or drag the orange brackets).
    // Cut out: mark a range ([ and ] at the playhead, or Shift + drag / drag in the lower half of the timeline), then X.
    // Del: brings the selected cut-out part back, or cuts out the marked range.

    private double? _markIn;

    [RelayCommand]
    private void TrimStartHere()
    {
        Edit.SetStart(Position);
        RaiseCut();
    }

    [RelayCommand]
    private void TrimEndHere()
    {
        Edit.SetEnd(Position);
        RaiseCut();
    }

    /// <summary>[: start of the range to cut out.</summary>
    [RelayCommand]
    private void MarkIn()
    {
        _markIn = Position;
        var end = Edit.Selection?.End is { } e && e > Position ? e : Math.Min(Duration, Position + 1);
        Edit.SetSelection(Position, end);
        RaiseCut();
    }

    /// <summary>]: end of the range to cut out.</summary>
    [RelayCommand]
    private void MarkOut()
    {
        var start = _markIn ?? Edit.Selection?.Start ?? Math.Max(0, Position - 1);
        Edit.SetSelection(start, Position);
        RaiseCut();
    }

    /// <summary>X: cut the marked range out.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CutOut()
    {
        var index = Edit.CutSelection();
        _markIn = null;
        SelectedRemoved = index;
        RaiseCut();
    }

    /// <summary>Brings the selected cut-out part back.</summary>
    [RelayCommand(CanExecute = nameof(HasRemovedSelected))]
    private void RestoreCut()
    {
        Edit.Restore(SelectedRemoved);
        SelectedRemoved = -1;
        RaiseCut();
    }

    /// <summary>Del: restore the selected cut-out part, otherwise cut out the marked range.</summary>
    [RelayCommand]
    private void DeleteKey()
    {
        if (HasRemovedSelected)
        {
            RestoreCut();
        }
        else if (HasSelection)
        {
            CutOut();
        }
    }

    [RelayCommand]
    private void ClearMark()
    {
        Edit.ClearSelection();
        _markIn = null;
        SelectedRemoved = -1;
        RaiseCut();
    }

    [RelayCommand]
    private void ResetCut()
    {
        Edit.Reset(Duration);
        _markIn = null;
        SelectedRemoved = -1;
        RaiseCut();
    }

    // ---------------------------------------------------------------- export

    [RelayCommand]
    private Task ExportAsync(string kind) => RunExportAsync(Enum.Parse<VideoExportKind>(kind));

    private async Task RunExportAsync(VideoExportKind kind)
    {
        var keep = Edit.Kept;
        if (IsBusy || keep.Count == 0)
        {
            Status = keep.Count == 0 ? Loc.T("VedNothingKept") : Status;
            return;
        }

        if (kind == VideoExportKind.Trim && Edit.IsUncut)
        {
            Status = Loc.T("VedNothingCut");
            return;
        }

        var ext = kind switch
        {
            VideoExportKind.Gif => ".gif",
            VideoExportKind.WebM => ".webm",
            _ => Path.GetExtension(File).Equals(".mkv", StringComparison.OrdinalIgnoreCase) ? ".mkv" : ".mp4"
        };

        // Trimmed video: next to the original without asking (copy _trim or overwrite – setting); others: save dialog.
        var overwrite = kind == VideoExportKind.Trim && _settings.TrimTarget == TrimTarget.Overwrite;
        string? target;
        if (kind == VideoExportKind.Trim)
        {
            target = overwrite
                ? Path.Combine(Path.GetDirectoryName(File)!, Path.GetFileNameWithoutExtension(File) + ".cut-tmp" + ext)
                : UniquePath(Path.Combine(Path.GetDirectoryName(File)!, Path.GetFileNameWithoutExtension(File) + "_trim" + ext));
        }
        else
        {
            var suffix = kind == VideoExportKind.Follow ? "_zoom" : "";
            target = PickTarget is null ? null : await PickTarget(Path.GetFileNameWithoutExtension(File) + suffix + ext, ext);
        }

        if (target is null)
        {
            return;
        }

        if (IsPlaying)
        {
            PlayPause();
        }

        IsBusy = true;
        Status = Loc.T("VedExporting");
        var temp = new List<string>();
        try
        {
            var workDir = Path.GetDirectoryName(target)!;
            IReadOnlyList<string> args;
            switch (kind)
            {
                case VideoExportKind.Gif:
                    args = RecordingArguments.Gif(File, keep, GifWidth?.Value ?? 640, 15, target);
                    break;
                case VideoExportKind.WebM:
                    args = RecordingArguments.WebM(File, keep, _settings.VideoQuality, target);
                    break;
                case VideoExportKind.Follow when _track is not null && Player.Info is { } info:
                    Directory.CreateDirectory(AppEnvironment.DataDirectory);
                    var commands = Path.Combine(AppEnvironment.DataDirectory, "follow.cmd");
                    temp.Add(commands);
                    await System.IO.File.WriteAllTextAsync(commands,
                        _track.ToCropCommands(FollowZoom, TimeSpan.FromSeconds(keep[0].Start), TimeSpan.FromSeconds(keep[^1].End)));
                    workDir = AppEnvironment.DataDirectory;
                    var zoomEncoder = await _capture.ResolveEncoderAsync(_ffmpeg) ?? "libx264";
                    args = RecordingArguments.FollowZoom(File, "follow.cmd", FollowZoom, info.Width, info.Height, keep, zoomEncoder, _settings.VideoQuality, target);
                    break;
                default:
                    if (ExactCut)
                    {
                        var encoder = await _capture.ResolveEncoderAsync(_ffmpeg) ?? "libx264";
                        args = RecordingArguments.CutExact(File, keep, Player.Info?.HasAudio ?? true, encoder, _settings.VideoQuality, target);
                    }
                    else if (keep.Count == 1)
                    {
                        // One part (trim only): a single copy from start to end.
                        args = RecordingArguments.Trim(File, TimeSpan.FromSeconds(keep[0].Start), TimeSpan.FromSeconds(keep[0].End), target, null,
                            _settings.VideoQuality);
                    }
                    else
                    {
                        // Several parts: copy each into its own file, then join them (robust with stream copy).
                        var work = Path.Combine(AppEnvironment.TempDirectory, "cut-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(work);
                        temp.Add(work);
                        var parts = new List<string>();
                        for (var i = 0; i < keep.Count; i++)
                        {
                            var part = Path.Combine(work, $"part{i}.mkv");
                            var (partOk, partError) = await RunAsync(RecordingArguments.ExtractPart(File, keep[i], part), work);
                            if (!partOk)
                            {
                                throw new IOException(partError);
                            }

                            parts.Add(part);
                        }

                        var list = Path.Combine(work, "parts.ffconcat");
                        await System.IO.File.WriteAllTextAsync(list, RecordingArguments.ConcatList(parts));
                        args = RecordingArguments.CutFast(list, target);
                    }

                    break;
            }

            var (ok, error) = await RunAsync(args, workDir);
            if (ok && overwrite)
            {
                // Overwrite: the original goes to the recycle bin first – the cut stays undoable.
                Player.Pause();
                await Task.Run(() =>
                {
                    CaptureTrash.MoveToTrash(File);
                    System.IO.File.Move(target, File);
                });
                target = File;

                // The file changed: read it again (new length, cut starts from scratch).
                await OpenAsync(_previewSize.Width, _previewSize.Height);
            }

            Status = ok ? Loc.F("VedExported", Path.GetFileName(target)) : Loc.F("VedFailed", error);
            if (ok)
            {
                Exported?.Invoke(target);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = Loc.F("VedFailed", e.Message);
        }
        finally
        {
            foreach (var file in temp)
            {
                try
                {
                    if (Directory.Exists(file))
                    {
                        Directory.Delete(file, recursive: true);
                    }
                    else
                    {
                        System.IO.File.Delete(file);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }

            if (overwrite && System.IO.File.Exists(target) && target != File)
            {
                try
                {
                    System.IO.File.Delete(target);
                }
                catch (IOException)
                {
                }
            }

            IsBusy = false;
        }
    }

    private static string UniquePath(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return path;
        }

        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{name}{i}{ext}");
            if (!System.IO.File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private async Task<(bool Ok, string Error)> RunAsync(IReadOnlyList<string> args, string workDir)
    {
        var info = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir
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
                return (false, "ffmpeg");
            }

            JobObject.Shared.Add(process);
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var last = error.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
            return (process.ExitCode == 0, last);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (false, e.Message);
        }
    }

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");
    }

    public void Dispose() => Player.Dispose();
}
