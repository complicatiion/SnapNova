using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using SnapNova.App.Localization;
using SnapNova.App.Overlay;
using SnapNova.App.Rendering;
using SnapNova.Core.Annotations;
using SnapNova.Core.Capture;
using SnapNova.Core.Library;
using SnapNova.Core.Media;
using SnapNova.Platform.Windows.Automation;
using SnapNova.Platform.Windows.Capture;
using SnapNova.Platform.Windows.Effects;
using SnapNova.Platform.Windows.Input;
using SnapNova.Platform.Windows.Ocr;
using SnapNova.Platform.Windows.Recording;
using SnapNova.Platform.Windows.Shell;

namespace SnapNova.App.Services;

/// <summary>
/// One capture at a time.
/// Images: (countdown) → freeze → overlay (Ctrl = element snapping) → .snova (+ clipboard, + export, + OCR) → editor.
/// Scrolling: overlay → automatic wheel scrolling or manual scrolling → stitched image.
/// Videos: FFmpeg consent/download → freeze → overlay → frame + toolbar (optional) → FFmpeg (MKV) → final format.
/// </summary>
public sealed class CaptureCoordinator(UiSettings settings, FfmpegGate ffmpeg, AppHost host) : ICaptureCoordinator, IDisposable
{
    private readonly ElementFinder? _elements = OperatingSystem.IsWindows() ? new ElementFinder() : null;
    private VideoRecorder? _recorder;
    private RecordingChrome? _chrome;
    private CursorEffects? _effects;
    private string? _pendingOutput;
    private bool _busy;
    private bool _addingSecondary;
    private readonly List<SecondaryRecording> _secondary = [];

    /// <summary>An additional recording next to the first one: own frame colour, own file, stopped together.</summary>
    private sealed class SecondaryRecording(int number, Color color, VideoRecorder recorder, RecordingChrome chrome, string output,
        string device, ScreenRect area, SecondaryAudio audio)
    {
        public string Device { get; } = device;
        public ScreenRect Area { get; } = area;
        public SecondaryAudio Audio { get; } = audio;
        public int Number { get; } = number;
        public Color Color { get; } = color;
        public VideoRecorder Recorder { get; } = recorder;
        public RecordingChrome Chrome { get; } = chrome;
        public string Output { get; } = output;
    }

    /// <summary>Up to four recordings at the same time (encoder sessions and CPU are the limit).</summary>
    private const int MaxRecordings = 4;

    public bool CanAddRecording => Recording is RecordingState.Recording or RecordingState.Paused
                                   && !_addingSecondary && _secondary.Count < MaxRecordings - 1;

    public bool IsBusy => _busy;

    public RecordingState Recording { get; private set; } = RecordingState.Idle;

    public event Action? StateChanged;

    /// <summary>A capture was saved (gallery refresh). Argument: file path.</summary>
    public event Action<string>? Saved;

    public async Task StartAsync(CaptureMode mode)
    {
        if (_busy || !OperatingSystem.IsWindows())
        {
            return;
        }

        SetBusy(true);
        settings.LastCaptureMode = mode;
        try
        {
            switch (mode)
            {
                case CaptureMode.VideoRegion or CaptureMode.VideoFullScreen:
                    await RecordAsync(mode);
                    break;
                case CaptureMode.ImageScrolling:
                    await ScrollingAsync();
                    break;
                case CaptureMode.ImageDelayed:
                    if (await CountdownWindow.RunAsync(settings.DelaySeconds))
                    {
                        await CaptureImageAsync(CaptureMode.ImageRegion, settle: false);
                    }

                    break;
                default:
                    await CaptureImageAsync(mode, settle: true);
                    break;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            host.ShowError(Loc.T("ErrCaptureTitle"), e.Message);
        }
        finally
        {
            if (Recording == RecordingState.Idle)
            {
                SetBusy(false);
            }
        }
    }

    // ================================================================ images

    private async Task CaptureImageAsync(CaptureMode mode, bool settle)
    {
        // Let a just-closed menu or the picker disappear before freezing.
        if (settle)
        {
            await Task.Delay(120);
        }

        var frozen = await Task.Run(() => ScreenCapture.Freeze(settings.IncludeCursor));
        SelectionResult? selection;
        if (mode == CaptureMode.ImageFullScreen)
        {
            var (cx, cy) = ScreenCapture.CursorPosition();
            var monitor = frozen.MonitorAt(cx, cy);
            selection = monitor is null ? null : new SelectionResult(monitor, new ScreenRect(0, 0, monitor.Bounds.Width, monitor.Bounds.Height), null);
        }
        else
        {
            selection = await SelectionOverlay.SelectAsync(frozen, windowOnly: mode == CaptureMode.ImageWindow, forVideo: false, _elements);
        }

        if (selection is null)
        {
            return;
        }

        var image = selection.Monitor.Image.Crop(selection.Area);
        var window = selection.Window ?? frozen.WindowAt(selection.ScreenArea.X + selection.Area.Width / 2, selection.ScreenArea.Y + selection.Area.Height / 2);
        await FinishImageAsync(image, new CaptureInfo(frozen.Taken, window?.ProcessName, window?.Title, selection.ScreenArea));
    }

    private async Task FinishImageAsync(PixelImage image, CaptureInfo info)
    {
        var path = await Task.Run(() => SaveImage(image, info));
        Saved?.Invoke(path);
        if (settings.OpenEditorAfterCapture)
        {
            host.OpenEditor(path);
        }

        // Text recognition in the background (gallery search, Smart Redact, copy text) – the editor reads it when needed.
        if (settings.RecognizeText && TextRecognizer.IsAvailable)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var ocr = await TextRecognizer.RecognizeAsync(image);
                    if (ocr is not null && SnovaFile.ReadDocument(path) is { } document && document.Ocr is null)
                    {
                        document.Ocr = ocr;
                        SnovaFile.UpdateDocument(path, document);
                        Dispatcher.UIThread.Post(() => host.TextRecognized(path, ocr));
                    }
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    // OCR is a bonus – the capture is saved anyway.
                }
            });
        }
    }

    /// <summary>Writes the .snova (original + empty annotation layer + thumbnail), optional export and clipboard.</summary>
    private string SaveImage(PixelImage image, CaptureInfo info)
    {
        var folder = settings.EffectiveImageFolder;
        Directory.CreateDirectory(folder);
        var path = CaptureLibrary.NewFileName(folder, SnovaFile.Extension, info.Created.LocalDateTime);
        using var bitmap = AnnotationRenderer.ToBitmap(image);
        var png = AnnotationRenderer.Encode(bitmap, SKEncodedImageFormat.Png);
        SnovaFile.Save(path, png, new AnnotationDocument { Info = info }, AnnotationRenderer.Thumbnail(bitmap));

        if (settings.AutoExportImage)
        {
            var (format, extension) = ImageExport.Format(settings.ImageFormat);
            File.WriteAllBytes(Path.ChangeExtension(path, extension), AnnotationRenderer.Encode(bitmap, format, settings.JpegQuality));
        }

        if (settings.CopyAfterCapture)
        {
            ClipboardImage.Set(image, png, settings.ExcludeFromClipboardHistory);
        }

        return path;
    }

    // ================================================================ scrolling capture

    private async Task ScrollingAsync()
    {
        await Task.Delay(120);
        var frozen = await Task.Run(() => ScreenCapture.Freeze(includeCursor: false));
        var selection = await SelectionOverlay.SelectAsync(frozen, windowOnly: false, forVideo: false, _elements, scrolling: true);
        if (selection is null || selection.Area.Height < 40)
        {
            return;
        }

        var area = selection.ScreenArea;
        var window = selection.Window ?? frozen.WindowAt(area.X + area.Width / 2, area.Y + area.Height / 2);
        var stitcher = new ScrollStitcher();
        var auto = settings.ScrollMode == CaptureScrollMode.Auto;
        var finished = false;
        var cancelled = false;

        var frame = new FrameStrips(area, selection.Monitor.Bounds);
        frame.Show(Color.Parse("#FF8C00"));
        var toolbar = new StatusToolbar(Loc.T(auto ? "ScrollAuto" : "ScrollManual"), Loc.T("ScrollDone"),
            () => finished = true, () => cancelled = true);
        toolbar.ShowNear(area, selection.Monitor.Bounds);
        var (oldX, oldY) = ScreenCapture.CursorPosition();

        try
        {
            await Task.Delay(250);
            stitcher.Add(await Task.Run(() => ScreenCapture.Grab(area, false)));
            while (!finished && !cancelled && !stitcher.IsFull)
            {
                if (MouseInput.IsDown(MouseInput.VkEscape))
                {
                    cancelled = true;
                    break;
                }

                if (MouseInput.IsDown(MouseInput.VkReturn))
                {
                    break;
                }

                if (auto)
                {
                    // Wheel over the middle of the area, then wait until smooth scrolling has settled.
                    MouseInput.MoveTo(area.X + area.Width / 2, area.Y + area.Height / 2);
                    MouseInput.Wheel(3);
                    await Task.Delay(380);
                }
                else
                {
                    await Task.Delay(180);
                }

                var added = stitcher.Add(await Task.Run(() => ScreenCapture.Grab(area, false)));
                toolbar.SetStatus(Loc.F("ScrollProgress", stitcher.Height));

                // Automatic: twice nothing new = end of the page.
                if (auto && added == 0 && stitcher.UnchangedFrames >= 2)
                {
                    break;
                }
            }
        }
        finally
        {
            if (auto)
            {
                MouseInput.MoveTo(oldX, oldY);
            }

            toolbar.Close();
            frame.Close();
        }

        if (cancelled)
        {
            return;
        }

        await FinishImageAsync(stitcher.Result(), new CaptureInfo(frozen.Taken, window?.ProcessName, window?.Title, area));
    }

    // ================================================================ video

    private async Task RecordAsync(CaptureMode mode)
    {
        if (await PrepareVideoAsync() is not { } prepared)
        {
            return;
        }

        var (exe, encoder) = prepared;

        await Task.Delay(120);
        var frozen = await Task.Run(() => ScreenCapture.Freeze(includeCursor: false));
        SelectionResult? selection;
        if (mode == CaptureMode.VideoFullScreen)
        {
            var (cx, cy) = ScreenCapture.CursorPosition();
            var monitor = frozen.MonitorAt(cx, cy);
            selection = monitor is null ? null : new SelectionResult(monitor, new ScreenRect(0, 0, monitor.Bounds.Width, monitor.Bounds.Height).EvenSize(), null);
        }
        else
        {
            selection = await SelectionOverlay.SelectAsync(frozen, windowOnly: false, forVideo: true, _elements);
        }

        if (selection is null || selection.Area.Width < 16 || selection.Area.Height < 16)
        {
            return;
        }

        var area = selection.Area.EvenSize();
        if (!await PreflightAsync(area.Offset(selection.Monitor.Bounds.X, selection.Monitor.Bounds.Y)))
        {
            return;
        }

        await StartPrimaryAsync(exe, encoder, selection.Monitor.DeviceName, selection.Monitor.Bounds, area);
    }

    /// <summary>ffmpeg.exe and the encoder (HEVC variant when MP4 · HEVC is chosen); null = not possible.</summary>
    private async Task<(string Exe, string Encoder)?> PrepareVideoAsync()
    {
        var exe = await ffmpeg.EnsureAsync(host.VisibleOwner());
        if (exe is null)
        {
            return null;
        }

        var encoder = await ResolveEncoderAsync(exe);
        if (encoder is null)
        {
            host.ShowError(Loc.T("ErrVideoTitle"), Loc.T("ErrNoEncoder"));
            return null;
        }

        return (exe, settings.VideoFormat == VideoFormat.Mp4Hevc ? RecordingArguments.ToHevc(encoder) : encoder);
    }

    /// <summary>Before a video: enough free space? Then the 3-2-1 countdown (setting). False = do not start.</summary>
    private async Task<bool> PreflightAsync(ScreenRect screenArea)
    {
        var free = FreeSpace(settings.EffectiveVideoFolder);
        if (free is { } bytes && bytes < LowSpaceBytes
            && !await host.Ask(Loc.T("DiskLowTitle"), Loc.F("DiskLowText", FormatGb(bytes)), Loc.T("DiskLowStart")))
        {
            return false;
        }

        if (settings.VideoCountdown)
        {
            var center = new Avalonia.PixelPoint(screenArea.X + screenArea.Width / 2, screenArea.Y + screenArea.Height / 2);
            return await CountdownWindow.RunAsync(3, center);
        }

        return true;
    }

    private const long LowSpaceBytes = 2L * 1024 * 1024 * 1024;
    private const long CriticalSpaceBytes = 400L * 1024 * 1024;

    private static long? FreeSpace(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string FormatGb(long bytes) => (bytes / 1024.0 / 1024 / 1024).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " GB";

    private string? _primaryDevice;
    private ScreenRect _primaryArea;
    private DispatcherTimer? _diskTimer;
    private DispatcherTimer? _toolbarSave;

    /// <summary>The main recording (frame, toolbar, cursor effects). Used by the region/full-screen capture and by templates.</summary>
    private async Task<bool> StartPrimaryAsync(string exe, string encoder, string device, ScreenRect monitorBounds, ScreenRect area)
    {
        var screenArea = area.Offset(monitorBounds.X, monitorBounds.Y);
        var folder = settings.EffectiveVideoFolder;
        Directory.CreateDirectory(folder);
        var output = CaptureLibrary.NewFileName(folder, settings.VideoFormat == VideoFormat.Mkv ? ".mkv" : ".mp4", DateTime.Now);

        // ddagrab (GPU) when the monitor is on the default adapter, else gdigrab. If ddagrab fails, retry once with gdigrab.
        var outputIndex = DxgiOutputs.OutputIndexOf(device);
        VideoSource source = outputIndex is { } idx
            ? new DdagrabSource(idx, area.X, area.Y)
            : new GdigrabSource(screenArea.X, screenArea.Y);

        Recording = RecordingState.Starting;
        StateChanged?.Invoke();
        var saved = settings.ToolbarX is { } tx && settings.ToolbarY is { } ty ? new Avalonia.PixelPoint(tx, ty) : (Avalonia.PixelPoint?)null;
        _chrome = new RecordingChrome(screenArea, monitorBounds, () => _recorder?.Elapsed ?? TimeSpan.Zero,
            TogglePause, () => _ = StopRecordingAsync(), () => _ = CancelRecordingAsync(),
            settings.ShowRecordingFrame, settings.ShowRecordingToolbar, RecordingChrome.SessionColors[0], () => _ = StartSecondaryAsync(),
            saved, SaveToolbarPosition);
        _chrome.Show();
        if (settings.CursorHighlight || settings.ClickEffects)
        {
            _effects = new CursorEffects(settings.CursorHighlight, settings.ClickEffects);
        }

        try
        {
            try
            {
                _recorder = await StartRecorderAsync(exe, encoder, source, area, screenArea, settings.RecordSystemSound, settings.RecordMicrophone);
            }
            catch (RecordingStartException) when (source is DdagrabSource)
            {
                // ddagrab is not available in every session (RDP, Citrix, some drivers): GDI fallback.
                _recorder = null;
            }

            _recorder ??= await StartRecorderAsync(exe, encoder, new GdigrabSource(screenArea.X, screenArea.Y), area, screenArea,
                settings.RecordSystemSound, settings.RecordMicrophone);
        }
        catch (RecordingStartException e)
        {
            CloseChrome();
            Recording = RecordingState.Idle;
            StateChanged?.Invoke();
            host.ShowError(Loc.T("ErrVideoTitle"), Loc.T("ErrRecordingStart") + "\n\n" + Tail(e.Log));
            settings.DetectedEncoder = null; // test again next time
            settings.Save();
            return false;
        }

        _pendingOutput = output;
        _primaryDevice = device;
        _primaryArea = area;
        _recorder.Failed += log => Dispatcher.UIThread.Post(() => _ = FailAsync(log));
        _recorder.DroppedFramesChanged += _ => Dispatcher.UIThread.Post(UpdateDropped);
        Recording = RecordingState.Recording;
        StateChanged?.Invoke();
        StartDiskWatch();

        if (_recorder.AudioWarnings.Count > 0)
        {
            host.ShowToast(Loc.T("WarnAudio") + " " + string.Join("; ", _recorder.AudioWarnings));
        }

        return true;
    }

    /// <summary>Dropped frames of all running recordings → warning sign in the toolbar.</summary>
    private void UpdateDropped() =>
        _chrome?.SetDropped((_recorder?.DroppedFrames ?? 0) + _secondary.Sum(s => s.Recorder.DroppedFrames));

    /// <summary>Toolbar dragged: remembered (saved after the mouse rests); null = back to automatic.</summary>
    private void SaveToolbarPosition(Avalonia.PixelPoint? position)
    {
        settings.ToolbarX = position?.X;
        settings.ToolbarY = position?.Y;
        _toolbarSave ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _toolbarSave!.Stop();
            settings.Save();
        });
        _toolbarSave.Stop();
        _toolbarSave.Start();
    }

    /// <summary>Hotkey: show or hide the toolbar of the running recording.</summary>
    public void ToggleToolbar() => _chrome?.ToggleToolbar();

    /// <summary>While recording: almost no space left → stop and save everything before the drive is full.</summary>
    private void StartDiskWatch()
    {
        _diskTimer?.Stop();
        _diskTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) =>
        {
            if (Recording is not (RecordingState.Recording or RecordingState.Paused))
            {
                _diskTimer?.Stop();
                return;
            }

            if (FreeSpace(settings.EffectiveVideoFolder) is { } free && free < CriticalSpaceBytes)
            {
                _diskTimer?.Stop();
                host.ShowToast(Loc.T("DiskFullStopped"));
                _ = StopRecordingAsync();
            }
        });
        _diskTimer.Start();
    }

    private async Task<VideoRecorder> StartRecorderAsync(string exe, string encoder, VideoSource source, ScreenRect area, ScreenRect screenArea,
        bool systemSound, bool microphone, int processId = 0)
    {
        var options = new RecordingOptions(source, area.Width, area.Height, settings.VideoFps, encoder, settings.VideoQuality,
            settings.VideoShowCursor, "", settings.VideoProfile, settings.Chroma444, settings.VideoOutputHeight);
        var work = Path.Combine(AppEnvironment.TempDirectory, "rec-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"));
        var recorder = new VideoRecorder(exe, options, work, systemSound, microphone, screenArea, processId);
        try
        {
            await recorder.StartAsync();
            return recorder;
        }
        catch
        {
            await recorder.DisposeAsync();
            throw;
        }
    }

    /// <summary>Configured encoder, or the first one that works on this PC (tested once, then remembered).</summary>
    public async Task<string?> ResolveEncoderAsync(string exe)
    {
        if (!string.Equals(settings.VideoEncoder, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return settings.VideoEncoder;
        }

        // 0.0.8 tests Media Foundation last: an older detection is repeated once.
        if (settings.EncoderOrderVersion < 2)
        {
            settings.EncoderOrderVersion = 2;
            settings.DetectedEncoder = null;
            settings.Save();
        }

        if (!string.IsNullOrEmpty(settings.DetectedEncoder))
        {
            return settings.DetectedEncoder;
        }

        host.ShowToast(Loc.T("ToastTestingEncoders"));
        var found = await FfmpegProbe.BestH264EncoderAsync(exe, CancellationToken.None);
        settings.DetectedEncoder = found;
        settings.Save();
        return found;
    }

    public void TogglePause()
    {
        if (_recorder is null || Recording is not (RecordingState.Recording or RecordingState.Paused))
        {
            return;
        }

        _ = TogglePauseAsync(_recorder);
    }

    private async Task TogglePauseAsync(VideoRecorder recorder)
    {
        try
        {
            // Pause and resume apply to all recordings at once.
            var resume = recorder.IsPaused;
            foreach (var r in new[] { recorder }.Concat(_secondary.Select(s => s.Recorder)))
            {
                if (resume && r.IsPaused)
                {
                    await r.ResumeAsync();
                }
                else if (!resume && !r.IsPaused)
                {
                    await r.PauseAsync();
                }
            }

            Recording = resume ? RecordingState.Recording : RecordingState.Paused;
            _chrome?.SetPaused(Recording == RecordingState.Paused);
            foreach (var secondary in _secondary)
            {
                secondary.Chrome.SetPaused(Recording == RecordingState.Paused);
            }
            StateChanged?.Invoke();
        }
        catch (RecordingStartException e)
        {
            await FailAsync(e.Log);
        }
    }

    public async Task StopRecordingAsync()
    {
        if (_recorder is not { } recorder || _pendingOutput is not { } output || Recording is RecordingState.Finishing or RecordingState.Idle)
        {
            return;
        }

        Recording = RecordingState.Finishing;
        StateChanged?.Invoke();
        _chrome?.SetBusy(Loc.T("RecSaving"));
        _effects?.Dispose();
        _effects = null;

        // "Stop" ends all recordings: the additional ones are saved first (in parallel).
        await Task.WhenAll(_secondary.ToList().Select(StopSecondaryCoreAsync));
        try
        {
            await recorder.StopAsync(output);
            Saved?.Invoke(output);
            if (settings.OpenGalleryAfterRecording)
            {
                host.ShowGallery();
            }
        }
        catch (RecordingStartException e)
        {
            var fallback = Path.ChangeExtension(output, ".mkv");
            if (File.Exists(fallback))
            {
                Saved?.Invoke(fallback);
            }

            host.ShowError(Loc.T("ErrVideoTitle"), e.Message + "\n\n" + Tail(e.Log));
        }
        finally
        {
            await EndRecordingAsync();
        }
    }

    private async Task CancelRecordingAsync()
    {
        if (_recorder is not { } recorder || Recording is RecordingState.Finishing or RecordingState.Idle)
        {
            return;
        }

        Recording = RecordingState.Finishing;
        StateChanged?.Invoke();
        foreach (var secondary in _secondary.ToList())
        {
            _secondary.Remove(secondary);
            secondary.Chrome.Close();
            await secondary.Recorder.CancelAsync();
            await secondary.Recorder.DisposeAsync();
        }

        await recorder.CancelAsync();
        await EndRecordingAsync();
    }

    private async Task FailAsync(string log)
    {
        if (Recording is RecordingState.Idle or RecordingState.Finishing)
        {
            return;
        }

        host.ShowError(Loc.T("ErrVideoTitle"), Loc.T("ErrRecordingStopped") + "\n\n" + Tail(log));
        await StopRecordingAsync();
    }

    private async Task EndRecordingAsync()
    {
        _diskTimer?.Stop();
        _primaryDevice = null;
        CloseChrome();
        if (_recorder is not null)
        {
            await _recorder.DisposeAsync();
            _recorder = null;
        }

        _pendingOutput = null;
        Recording = RecordingState.Idle;
        SetBusy(false);
    }

    // ================================================================ additional recordings

    /// <summary>
    /// Second (third, fourth) recording while one runs: region selection (the overlay is excluded from the running video),
    /// own frame colour, own file, sound per setting (none, program in the area, system). Stop ends all of them.
    /// </summary>
    public async Task StartSecondaryAsync()
    {
        if (!CanAddRecording || !OperatingSystem.IsWindows() || ffmpeg.Executable is not { } exe)
        {
            return;
        }

        _addingSecondary = true;
        _chrome?.SetCanAdd(false, RecordingCount, MaxRecordings);
        StateChanged?.Invoke();
        try
        {
            var encoder = await ResolveEncoderAsync(exe);
            if (encoder is null)
            {
                return;
            }

            if (settings.VideoFormat == VideoFormat.Mp4Hevc)
            {
                encoder = RecordingArguments.ToHevc(encoder);
            }

            var frozen = await Task.Run(() => ScreenCapture.Freeze(includeCursor: false));
            var selection = await SelectionOverlay.SelectAsync(frozen, windowOnly: false, forVideo: true, _elements, excludeFromCapture: true);
            if (selection is null || selection.Area.Width < 16 || selection.Area.Height < 16 || Recording is RecordingState.Finishing or RecordingState.Idle)
            {
                return;
            }

            var area = selection.Area.EvenSize();
            var screenArea = area.Offset(selection.Monitor.Bounds.X, selection.Monitor.Bounds.Y);

            // Sound of this recording: asked right here (default from the settings), or the setting directly.
            var audio = settings.SecondaryAudio;
            if (settings.AskSecondaryAudio)
            {
                var program = ProgramIn(selection.Window?.Handle ?? 0, screenArea);
                if (await AudioChoiceWindow.AskAsync(audio, program?.Name, screenArea) is not { } chosen)
                {
                    return;
                }

                audio = chosen;
            }

            await StartSecondaryCoreAsync(exe, encoder, selection.Monitor.DeviceName, selection.Monitor.Bounds, area, audio,
                selection.Window?.Handle ?? 0);
        }
        finally
        {
            _addingSecondary = false;
            _chrome?.SetCanAdd(CanAddRecording, RecordingCount, MaxRecordings);
            StateChanged?.Invoke();
        }
    }

    /// <summary>Running recordings (main + further ones).</summary>
    public int RecordingCount => (_recorder is null ? 0 : 1) + _secondary.Count;

    public int MaxRecordingCount => MaxRecordings;

    /// <summary>Starts one further recording on a known area (selection, template). False if it could not start.</summary>
    private async Task<bool> StartSecondaryCoreAsync(string exe, string encoder, string device, ScreenRect monitorBounds, ScreenRect area,
        SecondaryAudio audio, nint window = 0)
    {
        if (_secondary.Count >= MaxRecordings - 1)
        {
            return false;
        }

        var number = Enumerable.Range(2, MaxRecordings - 1).First(n => _secondary.All(s => s.Number != n));
        var color = RecordingChrome.SessionColors[number - 1];
        var screenArea = area.Offset(monitorBounds.X, monitorBounds.Y);
        var folder = settings.EffectiveVideoFolder;
        Directory.CreateDirectory(folder);
        var output = CaptureLibrary.PartFileName(folder, settings.VideoFormat == VideoFormat.Mkv ? ".mkv" : ".mp4", _pendingOutput, number);

        // Sound: the program in the area (process loopback), the whole system sound, or none.
        var processId = audio == SecondaryAudio.Application ? ProgramIn(window, screenArea)?.ProcessId ?? 0 : 0;
        var systemSound = audio == SecondaryAudio.System;

        var chrome = new RecordingChrome(screenArea, monitorBounds, () => TimeSpan.Zero, () => { }, () => { }, () => { },
            settings.ShowRecordingFrame, showToolbar: false, color);
        chrome.Show();

        var outputIndex = DxgiOutputs.OutputIndexOf(device);
        VideoSource source = outputIndex is { } idx ? new DdagrabSource(idx, area.X, area.Y) : new GdigrabSource(screenArea.X, screenArea.Y);
        VideoRecorder? recorder = null;
        try
        {
            try
            {
                recorder = await StartRecorderAsync(exe, encoder, source, area, screenArea, systemSound, settings.SecondaryMicrophone, processId);
            }
            catch (RecordingStartException) when (source is DdagrabSource)
            {
                recorder = null;
            }

            recorder ??= await StartRecorderAsync(exe, encoder, new GdigrabSource(screenArea.X, screenArea.Y), area, screenArea,
                systemSound, settings.SecondaryMicrophone, processId);
        }
        catch (RecordingStartException e)
        {
            chrome.Close();
            host.ShowError(Loc.T("ErrVideoTitle"), Loc.T("ErrRecordingStart") + "\n\n" + Tail(e.Log));
            return false;
        }

        // Running recordings are paused: the new one starts paused too.
        if (Recording == RecordingState.Paused)
        {
            await recorder.PauseAsync();
            chrome.SetPaused(true);
        }

        var session = new SecondaryRecording(number, color, recorder, chrome, output, device, area, audio);
        _secondary.Add(session);
        recorder.Failed += log => Dispatcher.UIThread.Post(() => _ = StopSecondaryAsync(session, log));
        recorder.DroppedFramesChanged += _ => Dispatcher.UIThread.Post(UpdateDropped);
        _chrome?.AddSession(number, color, () => _ = StopSecondaryAsync(session, null));
        if (recorder.AudioWarnings.Count > 0)
        {
            host.ShowToast(Loc.T("WarnAudio") + " " + string.Join("; ", recorder.AudioWarnings));
        }

        return true;
    }

    // ================================================================ templates (multi-recordings)

    /// <summary>Saves the running recordings (main + further ones) as a template; false when fewer than two run.</summary>
    public bool SaveTemplate(string name)
    {
        if (_primaryDevice is null || _secondary.Count == 0)
        {
            return false;
        }

        var template = new RecordingTemplate { Name = name };
        template.Areas.Add(new TemplateArea
        {
            Monitor = _primaryDevice, X = _primaryArea.X, Y = _primaryArea.Y, Width = _primaryArea.Width, Height = _primaryArea.Height
        });
        foreach (var s in _secondary.OrderBy(s => s.Number))
        {
            template.Areas.Add(new TemplateArea
            {
                Monitor = s.Device, X = s.Area.X, Y = s.Area.Y, Width = s.Area.Width, Height = s.Area.Height, Audio = s.Audio
            });
        }

        settings.Templates.RemoveAll(t => string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase));
        settings.Templates.Add(template);
        settings.Save();
        return true;
    }

    public bool CanSaveTemplate => _primaryDevice is not null && _secondary.Count > 0;

    /// <summary>
    /// Starts all areas of a template without selection: main recording first, then the further ones in parallel (a fraction of a
    /// second apart). Areas on monitors that are no longer connected are skipped with a notice.
    /// </summary>
    public async Task StartTemplateAsync(RecordingTemplate template)
    {
        if (_busy || Recording != RecordingState.Idle || template.Areas.Count == 0 || !OperatingSystem.IsWindows())
        {
            return;
        }

        SetBusy(true);
        try
        {
            if (await PrepareVideoAsync() is not { } prepared)
            {
                return;
            }

            var (exe, encoder) = prepared;
            var monitors = ScreenCapture.Monitors();
            var resolved = template.Areas
                .Select(a => (Area: a, Monitor: monitors.FirstOrDefault(m => m.DeviceName == a.Monitor)))
                .ToList();
            if (resolved.Any(r => r.Monitor is null))
            {
                host.ShowToast(Loc.T("TemplateMonitorMissing"));
            }

            var usable = resolved.Where(r => r.Monitor is not null).ToList();
            if (usable.Count == 0)
            {
                return;
            }

            ScreenRect Clamp(TemplateArea a, ScreenRect bounds) => new ScreenRect(
                Math.Clamp(a.X, 0, bounds.Width - 16), Math.Clamp(a.Y, 0, bounds.Height - 16),
                Math.Min(a.Width, bounds.Width - Math.Clamp(a.X, 0, bounds.Width - 16)),
                Math.Min(a.Height, bounds.Height - Math.Clamp(a.Y, 0, bounds.Height - 16))).EvenSize();

            var first = usable[0];
            var firstArea = Clamp(first.Area, first.Monitor!.Bounds);
            if (!await PreflightAsync(firstArea.Offset(first.Monitor.Bounds.X, first.Monitor.Bounds.Y)))
            {
                return;
            }

            if (!await StartPrimaryAsync(exe, encoder, first.Monitor.DeviceName, first.Monitor.Bounds, firstArea))
            {
                return;
            }

            _chrome?.SetCanAdd(false, RecordingCount, MaxRecordings);
            await Task.WhenAll(usable.Skip(1).Take(MaxRecordings - 1).Select(r =>
                StartSecondaryCoreAsync(exe, encoder, r.Monitor!.DeviceName, r.Monitor.Bounds, Clamp(r.Area, r.Monitor.Bounds), r.Area.Audio)));
            _chrome?.SetCanAdd(CanAddRecording, RecordingCount, MaxRecordings);
            StateChanged?.Invoke();
        }
        finally
        {
            // During a recording the coordinator stays busy (as with StartAsync).
            if (Recording == RecordingState.Idle)
            {
                SetBusy(false);
            }
        }
    }

    /// <summary>Program (name, process) of the selected window, else of the topmost window at the centre of the area.</summary>
    private static (string Name, int ProcessId)? ProgramIn(nint window, ScreenRect screenArea)
    {
        var windows = WindowList.Snapshot();
        var info = window != 0 ? windows.FirstOrDefault(w => w.Handle == window) : null;
        if (info is null)
        {
            var cx = screenArea.X + screenArea.Width / 2;
            var cy = screenArea.Y + screenArea.Height / 2;
            info = windows.FirstOrDefault(w => w.Bounds.Contains(cx, cy));
        }

        if (info is null)
        {
            return null;
        }

        var pid = WindowList.ProcessIdOf(info.Handle);
        return pid == 0 || pid == Environment.ProcessId ? null : (info.ProcessName, pid);
    }

    /// <summary>The small stop button of one additional recording (or its FFmpeg ended unexpectedly).</summary>
    private async Task StopSecondaryAsync(SecondaryRecording session, string? failLog)
    {
        if (!_secondary.Contains(session))
        {
            return;
        }

        if (failLog is not null)
        {
            host.ShowError(Loc.T("ErrVideoTitle"), Loc.T("ErrRecordingStopped") + "\n\n" + Tail(failLog));
        }

        await StopSecondaryCoreAsync(session);
        _chrome?.SetCanAdd(CanAddRecording, RecordingCount, MaxRecordings);
        StateChanged?.Invoke();
    }

    private async Task StopSecondaryCoreAsync(SecondaryRecording session)
    {
        _secondary.Remove(session);
        _chrome?.RemoveSession(session.Number);
        session.Chrome.Close();
        try
        {
            await session.Recorder.StopAsync(session.Output);
            Saved?.Invoke(session.Output);
        }
        catch (RecordingStartException e)
        {
            host.ShowError(Loc.T("ErrVideoTitle"), e.Message + "\n\n" + Tail(e.Log));
        }
        finally
        {
            await session.Recorder.DisposeAsync();
        }
    }

    private void CloseChrome()
    {
        _chrome?.Close();
        _chrome = null;
        _effects?.Dispose();
        _effects = null;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        StateChanged?.Invoke();
    }

    private static string Tail(string log)
    {
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('\n', lines.TakeLast(8));
    }

    public void Dispose()
    {
        _elements?.Dispose();
        _effects?.Dispose();
    }
}
