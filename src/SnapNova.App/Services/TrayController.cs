using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SnapNova.App.Localization;
using SnapNova.Core.Capture;

namespace SnapNova.App.Services;

/// <summary>
/// System tray. Left click runs the default action from the settings (picker, screenshot, video or gallery),
/// right click shows the full menu. While a video is recorded, the icon switches to the recording variant and the
/// first menu entries become Stop / Pause.
/// Ported from CLEANit's TrayController (same IconRenderer, Loc and menu style).
/// </summary>
public sealed class TrayController : IDisposable
{
    // Native tray menus follow the Windows menu theme, not the app theme: neutral icons, brand orange for captures.
    private static readonly Color IconColor = Color.Parse("#8A8A93");
    private static readonly Color AccentColor = Color.Parse("#FF8C00");
    private static readonly Color RecordColor = Color.Parse("#E53935");

    // The tray flyout and the taskbar hover state must be gone before the screen is frozen.
    private static readonly TimeSpan TraySettleDelay = TimeSpan.FromMilliseconds(150);

    private static readonly Lazy<Bitmap> Logo = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://SnapNova/Assets/tray-logo.png"));
        return new Bitmap(stream);
    });

    private readonly ICaptureCoordinator _capture;
    private readonly UiSettings _settings;
    private readonly TrayActions _actions;
    private readonly TrayIcon _trayIcon;
    private readonly WindowIcon _idleIcon;
    private readonly WindowIcon _recordingIcon;
    private readonly WindowIcon _pausedIcon;

    public TrayController(Application app, ICaptureCoordinator capture, UiSettings settings, TrayActions actions)
    {
        _capture = capture;
        _settings = settings;
        _actions = actions;
        _idleIcon = LoadIcon("tray.ico");
        _recordingIcon = LoadIcon("tray-recording.ico");
        _pausedIcon = LoadIcon("tray-paused.ico");

        _trayIcon = new TrayIcon { Icon = _idleIcon, ToolTipText = "SnapNova", IsVisible = true };
        _trayIcon.Clicked += (_, _) => OnLeftClick();
        TrayIcon.SetIcons(app, new TrayIcons { _trayIcon });

        _capture.StateChanged += Update;
        _settings.Changed += Update;
        Update();
    }

    /// <summary>Left click: default action. While recording: stop (default), pause/resume or menu – see the settings.</summary>
    private void OnLeftClick()
    {
        if (_capture.Recording is RecordingState.Recording or RecordingState.Paused)
        {
            switch (_settings.TrayRecordingClick)
            {
                case TrayRecordingClick.PauseResume:
                    _capture.TogglePause();
                    break;
                case TrayRecordingClick.Menu:
                    // Native tray menus cannot be opened from code in Avalonia: the right click shows it.
                    break;
                default:
                    _ = _capture.StopRecordingAsync();
                    break;
            }

            return;
        }

        if (_capture.IsBusy)
        {
            return;
        }

        switch (_settings.TrayDefaultAction)
        {
            case TrayDefaultAction.QuickPicker:
                _actions.ShowQuickPicker();
                break;
            case TrayDefaultAction.ImageRegion:
                Start(CaptureMode.ImageRegion);
                break;
            case TrayDefaultAction.VideoRegion:
                Start(CaptureMode.VideoRegion);
                break;
            case TrayDefaultAction.OpenGallery:
                _actions.ShowGallery();
                break;
        }
    }

    private void Start(CaptureMode mode)
    {
        if (_capture.IsBusy)
        {
            return;
        }

        _ = StartAfterSettleAsync(mode);
    }

    private async Task StartAfterSettleAsync(CaptureMode mode)
    {
        await Task.Delay(TraySettleDelay);
        await Dispatcher.UIThread.InvokeAsync(() => _capture.StartAsync(mode));
    }

    /// <summary>Short notice in the tray tooltip while no window is open (e.g. "Testing video encoders …").</summary>
    public void Notice(string text)
    {
        _trayIcon.ToolTipText = "SnapNova – " + text;
        DispatcherTimer.RunOnce(Update, TimeSpan.FromSeconds(8));
    }

    private void Update()
    {
        var recording = _capture.Recording is RecordingState.Recording or RecordingState.Paused;
        _trayIcon.Icon = _capture.Recording == RecordingState.Paused ? _pausedIcon : recording ? _recordingIcon : _idleIcon;
        _trayIcon.ToolTipText = recording
            ? "SnapNova – " + Loc.T(_capture.Recording == RecordingState.Paused ? "TrayPaused" : "TrayRecording") + " – " + Loc.T("TrayClick" + _settings.TrayRecordingClick)
            : "SnapNova – " + Loc.T("TrayTip" + _settings.TrayDefaultAction);
        _trayIcon.Menu = BuildMenu(recording);
    }

    private NativeMenu BuildMenu(bool recording)
    {
        var menu = new NativeMenu();
        var header = new NativeMenuItem("SnapNova  v" + AppEnvironment.Version) { Icon = Logo.Value };
        header.Click += (_, _) => _actions.ShowGallery();
        menu.Add(header);
        menu.Add(new NativeMenuItemSeparator());

        if (recording)
        {
            menu.Add(Item(Loc.T("TrayStop"), "IconStop", () => _ = _capture.StopRecordingAsync(), RecordColor));
            menu.Add(Item(Loc.T(_capture.Recording == RecordingState.Paused ? "TrayResume" : "TrayPause"),
                _capture.Recording == RecordingState.Paused ? "IconPlay" : "IconPause", _capture.TogglePause));
            if (_capture.CanAddRecording)
            {
                menu.Add(Item(Loc.F("TrayMoreRecording", _capture.RecordingCount + 1, _capture.MaxRecordingCount), "IconVideoPlus",
                    () => _ = _capture.StartSecondaryAsync()));
            }

            if (_actions.SaveTemplate is { } save && _actions.CanSaveTemplate?.Invoke() == true)
            {
                menu.Add(Item(Loc.T("TraySaveTemplate"), "IconTemplate", save));
            }

            menu.Add(Item(Loc.T("TrayToggleToolbar"), "IconDragGrip", _capture.ToggleToolbar));
            menu.Add(new NativeMenuItemSeparator());
        }
        else
        {
            // Captures, with the hotkey after a tab (Windows right-aligns it in native menus).
            menu.Add(Capture("TrayImageRegion", "IconSelection", CaptureMode.ImageRegion, AccentColor));
            menu.Add(Capture("TrayImageWindow", "IconWindow", CaptureMode.ImageWindow));
            menu.Add(Capture("TrayImageFullScreen", "IconMonitor", CaptureMode.ImageFullScreen));
            menu.Add(Capture("TrayImageScrolling", "IconScrollCapture", CaptureMode.ImageScrolling));
            menu.Add(Capture("TrayImageDelayed", "IconTimerCapture", CaptureMode.ImageDelayed));
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(Capture("TrayVideoRegion", "IconVideo", CaptureMode.VideoRegion, AccentColor));
            menu.Add(Capture("TrayVideoFullScreen", "IconVideoMonitor", CaptureMode.VideoFullScreen));
            if (_actions.StartTemplate is { } start && _settings.Templates.Count > 0)
            {
                var templates = new NativeMenu();
                foreach (var template in _settings.Templates)
                {
                    templates.Add(Item($"{template.Name}  ({template.Areas.Count})", "IconTemplate", () => start(template)));
                }

                menu.Add(Submenu(Loc.T("TrayTemplates"), "IconTemplate", templates));
            }

            menu.Add(new NativeMenuItemSeparator());
        }

        menu.Add(Item(Loc.T("TrayGallery"), "IconGallery", _actions.ShowGallery));
        menu.Add(Item(Loc.T("TrayOpenFolder"), "IconFolderOpen", _actions.OpenCaptureFolder));
        menu.Add(Submenu(Loc.T("TrayDefaultAction"), "IconCursorClick", BuildDefaultActionMenu()));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(Loc.T("NavSettings"), "IconCog", _actions.ShowSettings));
        menu.Add(Submenu(Loc.T("LblLanguage"), "IconLanguage", BuildLanguageMenu()));
        menu.Add(Item(Loc.T("NavAbout"), "IconInfo", _actions.ShowAbout));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(Loc.T("TrayExit"), "IconExit", _actions.Exit));
        return menu;
    }

    /// <summary>Left-click behaviour, switchable right here (✓ marks the current one).</summary>
    private NativeMenu BuildDefaultActionMenu()
    {
        var menu = new NativeMenu();
        foreach (var action in Enum.GetValues<TrayDefaultAction>())
        {
            var text = Loc.T("TrayAction" + action) + (action == _settings.TrayDefaultAction ? "  ✓" : "");
            menu.Add(Item(text, action == _settings.TrayDefaultAction ? "IconCheck" : "IconBlank", () =>
            {
                _settings.TrayDefaultAction = action;
                _settings.Save();
            }, action == _settings.TrayDefaultAction ? AccentColor : null));
        }

        return menu;
    }

    private NativeMenu BuildLanguageMenu()
    {
        var menu = new NativeMenu();
        foreach (var language in LanguageOption.All)
        {
            var code = language.Code;
            var active = language.IsAuto ? _settings.Language is null : _settings.Language == code;
            var header = active ? $"{language.Name}  ✓" : language.Name;
            var item = new NativeMenuItem(header) { Icon = language.Flag ?? IconRenderer.Render("IconLanguage", IconColor) };
            item.Click += (_, _) => _actions.SetLanguage(code);
            menu.Add(item);
        }

        return menu;
    }

    private NativeMenuItem Capture(string textKey, string iconKey, CaptureMode mode, Color? color = null)
    {
        var hotkey = _settings.HotkeyText(mode);
        var text = string.IsNullOrEmpty(hotkey) ? Loc.T(textKey) : Loc.T(textKey) + "\t" + hotkey;
        return Item(text, iconKey, () => Start(mode), color);
    }

    private static NativeMenuItem Item(string header, string iconKey, Action action, Color? color = null)
    {
        var item = new NativeMenuItem(header) { Icon = IconRenderer.Render(iconKey, color ?? IconColor) };
        item.Click += (_, _) => action();
        return item;
    }

    private static NativeMenuItem Submenu(string header, string iconKey, NativeMenu menu) =>
        new(header) { Icon = IconRenderer.Render(iconKey, IconColor), Menu = menu };

    private static WindowIcon LoadIcon(string file)
    {
        using var stream = AssetLoader.Open(new Uri("avares://SnapNova/Assets/" + file));
        return new WindowIcon(stream);
    }

    public void Dispose()
    {
        _capture.StateChanged -= Update;
        _settings.Changed -= Update;
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
    }
}

/// <summary>What the tray can open; implemented by App (window handling stays in one place).</summary>
public sealed record TrayActions(
    Action ShowGallery,
    Action ShowQuickPicker,
    Action ShowSettings,
    Action ShowAbout,
    Action OpenCaptureFolder,
    Action<string> SetLanguage,
    Action Exit,
    Action? SaveTemplate = null,
    Action<RecordingTemplate>? StartTemplate = null,
    Func<bool>? CanSaveTemplate = null);
