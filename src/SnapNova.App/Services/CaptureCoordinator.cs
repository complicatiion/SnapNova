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
    private sealed class SecondaryRecording(int number, Color color, VideoRecorder recorder, RecordingChrome chrome, string output)
    {
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
        var exe = await ffmpeg.EnsureAsync(host.VisibleOwner());
        if (exe is null)
        {
            return;
        }

        var encoder = await ResolveEncoderAsync(exe);
        if (encoder is null)
        {
            host.ShowError(Loc.T("ErrVideoTitle"), Loc.T("ErrNoEncoder"));
            return;
        }

        if (settings.VideoFormat == VideoFormat.Mp4Hevc)
        {
            encoder = RecordingArguments.ToHevc(encoder);
        }

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
        var screenArea = area.Offset(selection.Monitor.Bounds.X, selection.Monitor.Bounds.Y);
        var folder = settings.EffectiveVideoFolder;
        Directory.CreateDirectory(folder);
        var output = CaptureLibrary.NewFileName(folder, settings.VideoFormat == VideoFormat.Mkv ? ".mkv" : ".mp4", DateTime.Now);

        // ddagrab (GPU) when the monitor is on the default adapter, else gdigrab. If ddagrab fails, retry once with gdigrab.
        var outputIndex = DxgiOutputs.OutputIndexOf(selection.Monitor.DeviceName);
        VideoSource source = outputIndex is { } idx
            ? new DdagrabSource(idx, area.X, area.Y)
            : new GdigrabSource(screenArea.X, screenArea.Y);

        Recording = RecordingState.Starting;
        StateChanged?.Invoke();
        _chrome = new RecordingChrome(screenArea, selection.Monitor.Bounds, () => _recorder?.Elapsed ?? TimeSpan.Zero,
            TogglePause, () => _ = StopRecordingAsync(), () => _ = CancelRecordingAsync(),
            settings.ShowRecordingFrame, settings.ShowRecordingToolbar, RecordingChrome.SessionColors[0], () => _ = StartSecondaryAsync());
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
            return;
        }

        _pendingOutput = output;
        _recorder.Failed += log => Dispatcher.UIThread.Post(() => _ = FailAsync(log));
        Recording = RecordingState.Recording;
        StateChanged?.Invoke();

        if (_recorder.AudioWarnings.Count > 0)
        {
            host.ShowToast(Loc.T("WarnAudio") + " " + string.Join("; ", _recorder.AudioWarnings));
        }
    }

    private async Task<VideoRecorder> StartRecorderAsync(string exe, string encoder, VideoSource source, ScreenRect area, ScreenRect screenArea,
        bool systemSound, bool microphone, int processId = 0)
    {
        var options = new RecordingOptions(source, area.Width, area.Height, settings.VideoFps, encoder, settings.VideoQuality,
            settings.VideoShowCursor, "");
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
        _chrome?.SetCanAdd(false);
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

            var number = Enumerable.Range(2, MaxRecordings - 1).First(n => _secondary.All(s => s.Number != n));
            var color = RecordingChrome.SessionColors[number - 1];
            var area = selection.Area.EvenSize();
            var screenArea = area.Offset(selection.Monitor.Bounds.X, selection.Monitor.Bounds.Y);
            var folder = settings.EffectiveVideoFolder;
            Directory.CreateDirectory(folder);
            var output = CaptureLibrary.NewFileName(folder, settings.VideoFormat == VideoFormat.Mkv ? ".mkv" : ".mp4", DateTime.Now);
            output = Path.Combine(Path.GetDirectoryName(output)!, Path.GetFileNameWithoutExtension(output) + "_" + number + Path.GetExtension(output));

            // Sound: the program in the area (process loopback), the whole system sound, or none.
            var processId = settings.SecondaryAudio == SecondaryAudio.Application ? ProcessIn(selection, screenArea) : 0;
            var systemSound = settings.SecondaryAudio == SecondaryAudio.System;

            var chrome = new RecordingChrome(screenArea, selection.Monitor.Bounds, () => TimeSpan.Zero, () => { }, () => { }, () => { },
                settings.ShowRecordingFrame, showToolbar: false, color);
            chrome.Show();

            var outputIndex = DxgiOutputs.OutputIndexOf(selection.Monitor.DeviceName);
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
                return;
            }

            // Running recordings are paused: the new one starts paused too.
            if (Recording == RecordingState.Paused)
            {
                await recorder.PauseAsync();
                chrome.SetPaused(true);
            }

            var session = new SecondaryRecording(number, color, recorder, chrome, output);
            _secondary.Add(session);
            recorder.Failed += log => Dispatcher.UIThread.Post(() => _ = StopSecondaryAsync(session, log));
            _chrome?.AddSession(number, color, () => _ = StopSecondaryAsync(session, null));
            if (recorder.AudioWarnings.Count > 0)
            {
                host.ShowToast(Loc.T("WarnAudio") + " " + string.Join("; ", recorder.AudioWarnings));
            }
        }
        finally
        {
            _addingSecondary = false;
            _chrome?.SetCanAdd(CanAddRecording);
            StateChanged?.Invoke();
        }
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
        _chrome?.SetCanAdd(CanAddRecording);
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

    /// <summary>Process of the window in the selected area (selected window, else the topmost window at the centre).</summary>
    private static int ProcessIn(SelectionResult selection, ScreenRect screenArea)
    {
        var hwnd = selection.Window?.Handle ?? 0;
        if (hwnd == 0)
        {
            var cx = screenArea.X + screenArea.Width / 2;
            var cy = screenArea.Y + screenArea.Height / 2;
            hwnd = WindowList.Snapshot().FirstOrDefault(w => w.Bounds.Contains(cx, cy))?.Handle ?? 0;
        }

        if (hwnd == 0)
        {
            return 0;
        }

        var pid = WindowList.ProcessIdOf(hwnd);
        return pid == Environment.ProcessId ? 0 : pid;
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
