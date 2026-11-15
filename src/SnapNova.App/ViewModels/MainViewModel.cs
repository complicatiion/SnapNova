using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SnapNova.App.Services;
using SnapNova.Core.Capture;

namespace SnapNova.App.ViewModels;

/// <summary>One entry of the license list on the About page.</summary>
public sealed record LicenseEntry(string Name, string? Version, string License, string File, string? Note)
{
    public bool HasVersion => !string.IsNullOrEmpty(Version);

    public bool HasNote => !string.IsNullOrEmpty(Note);
}

/// <summary>About page: version, project links, own documents and third-party components as a list.</summary>
public sealed partial class AboutViewModel() : PageViewModel("NavAbout", "IconInfo")
{
    public string Version => "v" + AppEnvironment.Version;

    public IReadOnlyList<LicenseEntry> Documents { get; } =
    [
        new("SnapNova", null, "MIT", "LICENSE.md", null),
        new(Localization.Loc.T("AboutCopyright"), null, "Copyright", "COPYRIGHT.md", null),
        new(Localization.Loc.T("AboutLegal"), null, "Legal", "LEGAL-NOTICE.md", null),
        new(Localization.Loc.T("AboutPrivacyDoc"), null, "Privacy", "PRIVACY.md", null)
    ];

    public IReadOnlyList<LicenseEntry> Components { get; } =
    [
        new(".NET Runtime", "10.0", "MIT", "third-party/DOTNET.md", null),
        new("Avalonia UI", "11.3.14", "MIT", "third-party/AVALONIA.md", null),
        new("SkiaSharp / HarfBuzzSharp", null, "MIT · BSD-3", "third-party/SKIASHARP.md", null),
        new("CommunityToolkit.Mvvm", "8.4.0", "MIT", "third-party/COMMUNITYTOOLKIT-MVVM.md", null),
        new("NAudio", "2.2.1", "MIT", "third-party/NAUDIO.md", null),
        new("Material Design Icons", "7.4.47", "Apache-2.0", "third-party/MATERIAL-DESIGN-ICONS.md", null),
        new("Inter", "4.x", "OFL-1.1", "third-party/INTER-FONT.md", null),
        new("FFmpeg", null, "LGPL / GPL", "third-party/FFMPEG.md", Localization.Loc.T("AboutNotBundled"))
    ];

    [RelayCommand]
    private void OpenProject() => AppEnvironment.OpenUrl(AppEnvironment.ProjectUrl);

    [RelayCommand]
    private void OpenWebsite() => AppEnvironment.OpenUrl(AppEnvironment.WebsiteUrl);

    [RelayCommand]
    private void OpenLicenses() => AppEnvironment.OpenFolder(AppEnvironment.LicensesDirectory);

    [RelayCommand]
    private void OpenLicense(LicenseEntry? entry)
    {
        if (entry is not null)
        {
            AppEnvironment.OpenFile(Path.Combine(AppEnvironment.LicensesDirectory, entry.File.Replace('/', Path.DirectorySeparatorChar)));
        }
    }
}

/// <summary>Main window: icon rail, pages, toast.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _toastTimer;
    private readonly UiSettings _settings;

    public MainViewModel(GalleryViewModel gallery, GalleryViewModel trash, SettingsViewModel settings, AboutViewModel about, ICaptureCoordinator capture,
        UiSettings uiSettings)
    {
        Gallery = gallery;
        Trash = trash;
        gallery.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GalleryViewModel.FfmpegMissing))
            {
                OnPropertyChanged(nameof(ShowFfmpegPill));
            }
        };
        Settings = settings;
        About = about;
        Capture = capture;
        _settings = uiSettings;
        _currentPage = gallery;
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast = null;
        };
        capture.StateChanged += RaiseCaptureState;
        uiSettings.Changed += RaiseCaptureState;
        Localization.Loc.LanguageChanged += RaiseCaptureState;
    }

    public GalleryViewModel Gallery { get; }

    /// <summary>Recycle bin page (own rail entry above the settings).</summary>
    public GalleryViewModel Trash { get; }

    /// <summary>Header pill on the gallery: FFmpeg missing → click opens the setup.</summary>
    public bool ShowFfmpegPill => IsGallery && Gallery.FfmpegMissing;

    public SettingsViewModel Settings { get; }

    public AboutViewModel About { get; }

    public ICaptureCoordinator Capture { get; }

    public string WindowTitle => "SnapNova";

    public bool IsRecording => Capture.Recording is RecordingState.Recording or RecordingState.Paused;

    public Avalonia.Media.Geometry? VideoIcon => Res.Icon(IsRecording ? "IconStop" : "IconVideo");

    public bool IsGallery => ReferenceEquals(CurrentPage, Gallery);

    public bool IsTrash => ReferenceEquals(CurrentPage, Trash);

    public bool IsSettings => ReferenceEquals(CurrentPage, Settings);

    public bool IsAbout => ReferenceEquals(CurrentPage, About);

    public bool LastIsRegion => _settings.LastCaptureMode == CaptureMode.ImageRegion;

    public bool LastIsWindow => _settings.LastCaptureMode == CaptureMode.ImageWindow;

    public bool LastIsFullScreen => _settings.LastCaptureMode == CaptureMode.ImageFullScreen;

    public bool LastIsScrolling => _settings.LastCaptureMode == CaptureMode.ImageScrolling;

    public bool LastIsDelayed => _settings.LastCaptureMode == CaptureMode.ImageDelayed;

    public bool LastIsVideo => _settings.LastCaptureMode is CaptureMode.VideoRegion or CaptureMode.VideoFullScreen;

    public string TipRegion => Tip("PickRegion", CaptureMode.ImageRegion);

    public string TipWindow => Tip("PickWindow", CaptureMode.ImageWindow);

    public string TipFullScreen => Tip("PickFullScreen", CaptureMode.ImageFullScreen);

    public string TipScrolling => Tip("PickScrolling", CaptureMode.ImageScrolling);

    public string TipDelayed => Tip("PickDelayed", CaptureMode.ImageDelayed);

    public string TipVideo => IsRecording ? Localization.Loc.T("TrayStop") : Tip("PickVideo", CaptureMode.VideoRegion);

    [ObservableProperty]
    private PageViewModel _currentPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToast))]
    private string? _toast;

    [ObservableProperty]
    private bool _toastIsError;

    public bool HasToast => !string.IsNullOrEmpty(Toast);

    private string Tip(string key, CaptureMode mode)
    {
        var hotkey = _settings.HotkeyText(mode);
        return string.IsNullOrEmpty(hotkey) ? Localization.Loc.T(key) : $"{Localization.Loc.T(key)}  ({hotkey})";
    }

    private void RaiseCaptureState()
    {
        foreach (var name in new[]
                 {
                     nameof(IsRecording), nameof(VideoIcon), nameof(LastIsRegion), nameof(LastIsWindow), nameof(LastIsFullScreen),
                     nameof(LastIsScrolling), nameof(LastIsDelayed), nameof(LastIsVideo), nameof(TipRegion), nameof(TipWindow),
                     nameof(TipFullScreen), nameof(TipScrolling), nameof(TipDelayed), nameof(TipVideo), nameof(CanSecondRecording)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    public void NavigateTo(PageViewModel page)
    {
        CurrentPage = page;

        // Always raised: a click on the active rail button must not leave it unchecked.
        OnPropertyChanged(nameof(IsGallery));
        OnPropertyChanged(nameof(IsTrash));
        OnPropertyChanged(nameof(ShowFfmpegPill));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsAbout));
        page.OnShown();
    }

    public void ShowToast(string text, bool error = false)
    {
        Toast = text;
        ToastIsError = error;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    [RelayCommand]
    private void ShowGallery() => NavigateTo(Gallery);

    [RelayCommand]
    private void ShowTrash() => NavigateTo(Trash);

    /// <summary>Header button on the recycle bin page and rail context menu.</summary>
    [RelayCommand]
    private Task EmptyTrash() => Trash.EmptyTrashAsync();

    [RelayCommand]
    private void ShowSettings() => NavigateTo(Settings);

    /// <summary>Additional recording while one is running (own colour, stopped together with the first).</summary>
    [RelayCommand]
    private Task SecondRecording() => Capture.StartSecondaryAsync();

    public bool CanSecondRecording => IsRecording && Capture.CanAddRecording;

    [RelayCommand]
    private void ShowAbout() => NavigateTo(About);

    [RelayCommand]
    private void CloseToast() => Toast = null;

    [RelayCommand]
    private Task CaptureImage() => Capture.StartAsync(CaptureMode.ImageRegion);

    [RelayCommand]
    private Task CaptureWindow() => Capture.StartAsync(CaptureMode.ImageWindow);

    [RelayCommand]
    private Task CaptureFullScreen() => Capture.StartAsync(CaptureMode.ImageFullScreen);

    [RelayCommand]
    private Task CaptureScrolling() => Capture.StartAsync(CaptureMode.ImageScrolling);

    [RelayCommand]
    private Task CaptureDelayed() => Capture.StartAsync(CaptureMode.ImageDelayed);

    /// <summary>Starts a video, or stops the running one.</summary>
    [RelayCommand]
    private Task Video() => IsRecording ? Capture.StopRecordingAsync() : Capture.StartAsync(CaptureMode.VideoRegion);
}
