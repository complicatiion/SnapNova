using Avalonia.Threading;
using SnapNova.Core.Capture;
using SnapNova.Platform.Windows.Input;

namespace SnapNova.App.Services;

/// <summary>
/// Registers the hotkeys from the settings (again after every change) and starts captures. The video hotkey stops a
/// running recording – one key to start, the same key to stop.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly UiSettings _settings;
    private readonly ICaptureCoordinator _capture;
    private const int PauseId = 1000;
    private const int PrintId = 1001;
    private readonly HotkeyService? _service;
    private string _registered = "";

    public HotkeyManager(UiSettings settings, ICaptureCoordinator capture)
    {
        _settings = settings;
        _capture = capture;
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _service = new HotkeyService();
        _service.Pressed += id => Dispatcher.UIThread.Post(() =>
        {
            if (id == PauseId)
            {
                _capture.TogglePause();
            }
            else if (id == PrintId)
            {
                if (_settings.PrintKeyMode is { } printMode)
                {
                    OnPressed(printMode);
                }
            }
            else
            {
                OnPressed((CaptureMode)id);
            }
        });
        _settings.Changed += () => _ = ApplyAsync();
    }

    /// <summary>Hotkeys Windows refused (taken by another app) – shown in Settings › Hotkeys.</summary>
    public IReadOnlyList<CaptureMode> Failed { get; private set; } = [];

    public bool PauseFailed { get; private set; }

    /// <summary>The Print key could not be registered (usually Windows' own snipping owns it).</summary>
    public bool PrintFailed { get; private set; }

    public event Action? FailedChanged;

    public async Task ApplyAsync()
    {
        if (_service is null)
        {
            return;
        }

        // Settings.Changed fires for every setting – only re-register when the hotkeys changed.
        var signature = string.Join('|', _settings.Hotkeys.OrderBy(h => h.Key).Select(h => $"{h.Key}={h.Value}")) + "|pause=" + _settings.PauseHotkey
                        + "|print=" + _settings.PrintKeyMode;
        if (signature == _registered)
        {
            return;
        }

        _registered = signature;
        var keys = Enum.GetValues<CaptureMode>().Select(m => ((int)m, _settings.HotkeyFor(m))).ToList();
        keys.Add((PauseId, Hotkey.Parse(_settings.PauseHotkey)));

        // Print key (no modifiers) for the capture chosen in Settings › Capture.
        keys.Add((PrintId, _settings.PrintKeyMode is null ? default : Hotkey.Parse("PrintScreen")));
        var result = await _service.SetAsync(keys);
        Failed = result.Where(r => !r.Value && r.Key != PauseId && r.Key != PrintId).Select(r => (CaptureMode)r.Key).ToList();
        PrintFailed = _settings.PrintKeyMode is not null && result.TryGetValue(PrintId, out var printOk) && !printOk;
        PauseFailed = result.TryGetValue(PauseId, out var pauseOk) && !pauseOk;
        Dispatcher.UIThread.Post(() => FailedChanged?.Invoke());
    }

    private void OnPressed(CaptureMode mode)
    {
        if (mode.IsVideo() && _capture.Recording is RecordingState.Recording or RecordingState.Paused)
        {
            _ = _capture.StopRecordingAsync();
            return;
        }

        if (!_capture.IsBusy)
        {
            _ = _capture.StartAsync(mode);
        }
    }

    public void Dispose() => _service?.Dispose();
}
