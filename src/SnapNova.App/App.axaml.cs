using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SnapNova.App.Localization;
using SnapNova.App.Overlay;
using SnapNova.App.Services;
using SnapNova.App.ViewModels;
using SnapNova.App.Views;
using SnapNova.Core.Capture;
using SnapNova.Core.Library;
using SnapNova.Core.Media;

namespace SnapNova.App;

/// <summary>Composition root: services, tray, hotkeys, windows. Runs in the tray until Exit.</summary>
public partial class App : Application
{
    private readonly Dictionary<string, EditorWindow> _editors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VideoEditorWindow> _videoEditors = new(StringComparer.OrdinalIgnoreCase);
    private FfmpegGate? _gate;
    private UpdateService? _updates;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private UiSettings _settings = new();
    private MainWindow? _main;
    private MainViewModel? _vm;
    private TrayController? _tray;
    private HotkeyManager? _hotkeys;
    private CaptureCoordinator? _capture;
    private HttpClient? _http;
    private QuickPicker? _picker;
    private bool _exiting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Start()
    {
        // Setup choices (autostart, tray) once per Windows user – before the settings are loaded.
        FirstRun.ApplySetupDefaults(AppEnvironment.ExecutablePath, force: false);
        _settings = UiSettings.Load();
        Loc.Apply(_settings.Language ?? Loc.Detect());
        ThemeService.Apply(_settings.Theme);

        // Animations (Settings › General) and exact text sizes for selecting texts in the editor.
        Controls.Motion.Enabled = _settings.Animations;
        _settings.Changed += () => Controls.Motion.Enabled = _settings.Animations;
        Core.Annotations.TextShape.Measurer = Rendering.AnnotationRenderer.MeasureText;

        // .snova opens in SnapNova also without the setup (never overrides a working association; portable copies stay clean).
        if (OperatingSystem.IsWindows() && !AppEnvironment.IsPortable)
        {
            _ = Task.Run(() => SnapNova.Platform.Windows.Shell.FileAssociation.EnsureRegistered(AppEnvironment.ExecutablePath, Loc.T("FileTypeName")));
        }

        // Network use of SnapNova (both opt-in): the FFmpeg download after consent, the update check if switched on.
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SnapNova/" + AppEnvironment.Version);

        var host = new AppHost();
        var provisioner = new FfmpegProvisioner(AppEnvironment.DataDirectory, _http);
        _gate = new FfmpegGate(provisioner, _settings);
        _capture = new CaptureCoordinator(_settings, _gate, host);
        _hotkeys = new HotkeyManager(_settings, _capture);
        _updates = new UpdateService(_settings, _http, host);

        host.FfmpegInstalled = () => _gate.IsInstalled;
        _gate.Installed += host.RaiseFfmpegChanged;
        var thumbnails = new ThumbnailService(provisioner);
        var gallery = new GalleryViewModel(_settings, thumbnails, host, mode => _capture.StartAsync(mode));
        var trash = new GalleryViewModel(_settings, thumbnails, host, mode => _capture.StartAsync(mode), trashPage: true);
        var settingsPage = new SettingsViewModel(_settings, _hotkeys, provisioner, _gate, host, _updates);
        _vm = new MainViewModel(gallery, trash, settingsPage, new AboutViewModel(), _capture, _settings);
        _main = new MainWindow { DataContext = _vm, Settings = _settings };
        _main.ExitRequested += () => _ = ExitAsync();
        _capture.Saved += gallery.OnSaved;

        host.ShowGallery = () => ShowMain(gallery);
        host.ShowSettings = () => ShowMain(settingsPage);
        host.ShowAbout = () => ShowMain(_vm.About);
        host.OpenEditor = OpenEditor;
        host.VisibleOwner = () => _main is { IsVisible: true } ? _main : null;
        host.ShowError = (title, message) => _ = DialogWindow.ShowAsync(host.VisibleOwner(), title, message);
        host.Confirm = (title, message, confirm) => DialogWindow.ShowAsync(host.VisibleOwner(), title, message, confirm, danger: true);
        host.Ask = (title, message, confirm) => DialogWindow.ShowAsync(host.VisibleOwner(), title, message, confirm, icon: "IconUpdate");
        host.Input = (title, value, confirm) => DialogWindow.InputAsync(host.VisibleOwner(), title, value, confirm);
        host.TextRecognized = (path, ocr) => Dispatcher.UIThread.Post(() =>
        {
            gallery.OnTextRecognized(path, ocr.FullText);
            if (_editors.TryGetValue(path, out var editor) && editor.DataContext is EditorViewModel evm)
            {
                evm.ApplyRecognizedText(ocr);
            }
        });
        host.PlayVideo = path => _ = PlayVideoAsync(path);
        host.OpenFfmpegSettings = () =>
        {
            ShowMain(settingsPage);
            settingsPage.ScrollTo("FfmpegCard");
        };
        host.EditVideo = path => _ = OpenVideoEditorAsync(path);
        host.ShowToast = text =>
        {
            if (_main is { IsVisible: true })
            {
                _vm.ShowToast(text);
            }
            else
            {
                _tray?.Notice(text);
            }
        };

        _tray = new TrayController(this, _capture, _settings, new TrayActions(
            ShowGallery: () => ShowMain(gallery),
            ShowQuickPicker: ShowQuickPicker,
            ShowSettings: () => ShowMain(settingsPage),
            ShowAbout: () => ShowMain(_vm.About),
            OpenCaptureFolder: () => AppEnvironment.OpenFolder(_settings.EffectiveImageFolder, create: true),
            SetLanguage: code =>
            {
                _settings.Language = code == LanguageOption.AutoCode ? null : code;
                Loc.Apply(_settings.Language ?? Loc.Detect());
                _settings.Save();
            },
            Exit: () => _ = ExitAsync()));

        _ = _hotkeys.ApplyAsync();
        _ = _updates.CheckOnStartAsync();
        Program.Forwarded += request => Dispatcher.UIThread.Post(() => Handle(request));
        Handle(Program.Initial);

        // First start: tell where SnapNova lives (the window is open then – started by the installer or the user).
        if (!_settings.TrayHintShown && _main.IsVisible)
        {
            _settings.TrayHintShown = true;
            _settings.Save();
            Dispatcher.UIThread.Post(() => _vm.ShowToast(Loc.T("ToastTrayHint")), DispatcherPriority.Background);
        }
    }

    /// <summary>Start arguments of this or a forwarded second start.</summary>
    private void Handle(StartRequest request)
    {
        if (request.File is { } file)
        {
            if (CaptureLibrary.KindOf(file) == LibraryItemKind.Video)
            {
                _ = PlayVideoAsync(file);
            }
            else
            {
                OpenEditor(file);
            }

            return;
        }

        if (request.Capture is { } mode)
        {
            _ = _capture!.StartAsync(mode);
            return;
        }

        // --tray (autostart): stay in the tray. Any other start shows the gallery.
        if (!request.Tray)
        {
            ShowMain(_vm!.Gallery);
        }
    }

    private void ShowMain(PageViewModel page)
    {
        if (_main is null || _vm is null)
        {
            return;
        }

        _vm.NavigateTo(page);
        if (!_main.IsVisible)
        {
            _main.Show();
        }

        if (_main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = _settings.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        }

        _main.Activate();
    }

    private void ShowQuickPicker()
    {
        _picker?.Close();
        _picker = new QuickPicker(mode => _ = _capture!.StartAsync(mode));
        _picker.Closed += (_, _) => _picker = null;
        _picker.ShowAtCursor();
    }

    private void OpenEditor(string path)
    {
        if (_editors.TryGetValue(path, out var open))
        {
            open.Activate();
            return;
        }

        EditorViewModel vm;
        try
        {
            vm = EditorViewModel.Open(_settings, path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            _ = DialogWindow.ShowAsync(_main is { IsVisible: true } ? _main : null, Loc.T("ErrOpenTitle"), e.Message);
            return;
        }

        vm.Saved += saved => _vm?.Gallery.OnSaved(saved);
        var window = new EditorWindow(vm);
        _editors[path] = window;
        window.Closed += (_, _) => _editors.Remove(path);
        window.Show();
        window.Activate();
    }

    /// <summary>Video player per setting: Windows default app, own program, or SnapNova's preview (needs FFmpeg).</summary>
    private async Task PlayVideoAsync(string path)
    {
        switch (_settings.Player)
        {
            // SnapNova player needs FFmpeg: until it is installed, the Windows player is used (no dialog on a double-click).
            case PlayerMode.Internal when _gate?.IsInstalled == true:
                await OpenVideoEditorAsync(path);
                break;
            case PlayerMode.Custom when File.Exists(_settings.CustomPlayer):
                try
                {
                    var info = new System.Diagnostics.ProcessStartInfo(_settings.CustomPlayer!) { UseShellExecute = false };
                    info.ArgumentList.Add(path);
                    System.Diagnostics.Process.Start(info)?.Dispose();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    AppEnvironment.OpenFile(path);
                }

                break;
            default:
                AppEnvironment.OpenFile(path);
                break;
        }
    }

    private async Task OpenVideoEditorAsync(string path)
    {
        if (_videoEditors.TryGetValue(path, out var open))
        {
            open.Activate();
            return;
        }

        // Preview and export run on the user's ffmpeg.exe (opt-in download as for recording).
        if (_gate is null || _capture is null || await _gate.EnsureAsync(_main is { IsVisible: true } ? _main : null) is not { } ffmpeg)
        {
            return;
        }

        var vm = new VideoEditorViewModel(path, ffmpeg, _settings, _capture);
        vm.Exported += saved => _vm?.Gallery.OnSaved(saved);
        var window = new VideoEditorWindow(vm);
        _videoEditors[path] = window;
        window.Closed += (_, _) => _videoEditors.Remove(path);
        window.Show();
        window.Activate();
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;

        // A running recording is finished and saved, not thrown away.
        if (_capture is { Recording: RecordingState.Recording or RecordingState.Paused })
        {
            await _capture.StopRecordingAsync();
        }

        foreach (var editor in _editors.Values.ToList())
        {
            editor.Close(); // saves changes
        }

        foreach (var video in _videoEditors.Values.ToList())
        {
            video.Close();
        }

        if (_main is not null)
        {
            _main.AllowClose = true;
            _main.Close();
        }

        _settings.Save();
        _tray?.Dispose();
        _hotkeys?.Dispose();
        _capture?.Dispose();
        _http?.Dispose();
        _desktop?.Shutdown();
    }
}
