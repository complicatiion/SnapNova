using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SnapNova.App.Localization;
using SnapNova.App.Services;
using SnapNova.Core.Annotations;
using SnapNova.Core.Capture;
using SnapNova.Core.Media;
using SnapNova.Platform.Windows.Startup;
using ImageKind = SnapNova.App.Services.ImageFormat;
using RedactKindMode = SnapNova.Core.Annotations.RedactMode;
using VideoKind = SnapNova.App.Services.VideoFormat;

namespace SnapNova.App.ViewModels;

public sealed record Choice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>A saved multi-recording in the settings: the name can be edited in place.</summary>
public sealed partial class TemplateItem(RecordingTemplate template, Action save) : ObservableObject
{
    public RecordingTemplate Template { get; } = template;

    public string Name
    {
        get => Template.Name;
        set
        {
            if (value != Template.Name && value.Trim().Length > 0)
            {
                Template.Name = value.Trim();
                OnPropertyChanged();
                save();
            }
        }
    }

    public string AreasText => Loc.F("TemplateAreas", Template.Areas.Count);
}

/// <summary>Settings page. Every change is saved at once (no Apply button).</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly UiSettings _settings;
    private readonly HotkeyManager _hotkeys;
    private readonly FfmpegProvisioner _ffmpeg;
    private readonly FfmpegGate _gate;
    private readonly AppHost _host;
    private readonly UpdateService _updates;
    private bool _loading;

    public SettingsViewModel(UiSettings settings, HotkeyManager hotkeys, FfmpegProvisioner ffmpeg, FfmpegGate gate, AppHost host, UpdateService updates)
        : base("NavSettings", "IconCog")
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _ffmpeg = ffmpeg;
        _gate = gate;
        _host = host;
        _updates = updates;
        _hotkeys.FailedChanged += () =>
        {
            OnPropertyChanged(nameof(HotkeyWarning));
            OnPropertyChanged(nameof(PrintKeyBlocked));
        };
        _updates.StateChanged += () =>
        {
            OnPropertyChanged(nameof(UpdateStatus));
            OnPropertyChanged(nameof(HasUpdate));
        };
        Load();
    }

    /// <summary>Set by the view: folder and file pickers.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    public Func<Task<string?>>? PickProgram { get; set; }

    public IReadOnlyList<LanguageOption> Languages => LanguageOption.All;

    public IReadOnlyList<Choice<string>> Themes { get; } =
        [new("dark", Loc.T("ThemeDark")), new("light", Loc.T("ThemeLight")), new("system", Loc.T("ThemeSystem"))];

    public IReadOnlyList<Choice<DisplayProfile>> Displays { get; } = Enum.GetValues<DisplayProfile>()
        .Select(d => new Choice<DisplayProfile>(d, Loc.T(d switch
        {
            DisplayProfile.ExtraSmall => "DisplayXS",
            DisplayProfile.Small => "DisplayS",
            DisplayProfile.Medium => "DisplayM",
            DisplayProfile.Large => "DisplayL",
            DisplayProfile.ExtraLarge => "DisplayXL",
            _ => "DisplayAuto"
        }))).ToList();

    public IReadOnlyList<Choice<TrayDefaultAction>> TrayActions { get; } = Enum.GetValues<TrayDefaultAction>()
        .Select(a => new Choice<TrayDefaultAction>(a, Loc.T("TrayAction" + a))).ToList();

    public IReadOnlyList<Choice<TrayRecordingClick>> RecordingClicks { get; } = Enum.GetValues<TrayRecordingClick>()
        .Select(a => new Choice<TrayRecordingClick>(a, Loc.T("RecClick" + a))).ToList();

    public IReadOnlyList<Choice<StorageMode>> StorageModes { get; } =
        [new(StorageMode.Separate, Loc.T("StorageSeparate")), new(StorageMode.Common, Loc.T("StorageCommon"))];

    public IReadOnlyList<Choice<ImageFormat>> ImageFormats { get; } =
        [new(ImageKind.Png, "PNG"), new(ImageKind.Jpeg, "JPEG"), new(ImageKind.Webp, "WebP")];

    public IReadOnlyList<Choice<VideoFormat>> VideoFormats { get; } =
        [new(VideoKind.Mp4H264, "MP4 · H.264"), new(VideoKind.Mp4Hevc, "MP4 · HEVC (H.265)"), new(VideoKind.Mkv, "MKV · H.264")];

    public IReadOnlyList<Choice<PlayerMode>> Players { get; } =
        [new(PlayerMode.Internal, Loc.T("PlayerInternal")), new(PlayerMode.Windows, Loc.T("PlayerWindows")), new(PlayerMode.Custom, Loc.T("PlayerCustom"))];

    public IReadOnlyList<Choice<TrashMode>> TrashModes { get; } =
        [new(Services.TrashMode.SnapNova, Loc.T("TrashModeSnapNova")), new(Services.TrashMode.Windows, Loc.T("TrashModeWindows"))];

    public IReadOnlyList<Choice<TrimTarget>> TrimTargets { get; } =
        [new(Services.TrimTarget.Copy, Loc.T("TrimCopy")), new(Services.TrimTarget.Overwrite, Loc.T("TrimOverwrite"))];

    public IReadOnlyList<Choice<SecondaryAudio>> SecondaryAudios { get; } =
    [
        new(Services.SecondaryAudio.None, Loc.T("SecAudioNone")),
        new(Services.SecondaryAudio.Application, Loc.T("SecAudioApp")),
        new(Services.SecondaryAudio.System, Loc.T("SecAudioSystem"))
    ];

    public IReadOnlyList<Choice<RedactMode>> RedactModes { get; } =
        [new(RedactKindMode.Blur, Loc.T("RedactModeBlur")), new(RedactKindMode.Pixelate, Loc.T("RedactModePixelate")), new(RedactKindMode.Bar, Loc.T("RedactModeBar"))];

    public IReadOnlyList<Choice<CaptureScrollMode>> ScrollModes { get; } =
        [new(CaptureScrollMode.Auto, Loc.T("ScrollModeAuto")), new(CaptureScrollMode.Manual, Loc.T("ScrollModeManual"))];

    /// <summary>Print key: off or one of the captures.</summary>
    public IReadOnlyList<Choice<CaptureMode?>> PrintKeyChoices { get; } =
    [
        new(null, Loc.T("PrintKeyOff")),
        new(CaptureMode.ImageRegion, Loc.T("PickRegion")),
        new(CaptureMode.ImageWindow, Loc.T("PickWindow")),
        new(CaptureMode.ImageFullScreen, Loc.T("PickFullScreen")),
        new(CaptureMode.ImageScrolling, Loc.T("PickScrolling")),
        new(CaptureMode.ImageDelayed, Loc.T("PickDelayed")),
        new(CaptureMode.VideoRegion, Loc.T("PickVideo"))
    ];

    public IReadOnlyList<Choice<VideoProfile>> Profiles { get; } =
        [new(VideoProfile.Screen, Loc.T("ProfileScreen")), new(VideoProfile.Motion, Loc.T("ProfileMotion"))];

    public IReadOnlyList<Choice<int>> OutputHeights { get; } =
        [new(0, Loc.T("OutputNative")), new(1440, "1440p"), new(1080, "1080p"), new(720, "720p")];

    public System.Collections.ObjectModel.ObservableCollection<TemplateItem> Templates { get; } = [];

    public bool HasTemplates => Templates.Count > 0;

    public bool HasToolbarPosition => _settings.ToolbarX is not null;

    public IReadOnlyList<Choice<int>> Delays { get; } = [new(3, "3 s"), new(5, "5 s"), new(10, "10 s")];

    public IReadOnlyList<Choice<int>> Retentions { get; } =
        [new(7, Loc.F("Days", 7)), new(14, Loc.F("Days", 14)), new(30, Loc.F("Days", 30)), new(60, Loc.F("Days", 60)), new(90, Loc.F("Days", 90)),
            new(180, Loc.F("Days", 180)), new(0, Loc.T("RetentionForever"))];

    public IReadOnlyList<Choice<int>> FpsChoices { get; } = [new(15, "15 fps"), new(24, "24 fps"), new(30, "30 fps"), new(60, "60 fps")];

    public IReadOnlyList<Choice<string>> Encoders { get; } =
    [
        new("auto", Loc.T("EncAuto")),
        new("h264_nvenc", "NVIDIA NVENC"),
        new("h264_qsv", "Intel Quick Sync"),
        new("h264_amf", "AMD AMF"),
        new("h264_mf", "Media Foundation"),
        new("libx264", Loc.T("EncX264"))
    ];

    // ---------------------------------------------------------------- general
    [ObservableProperty] private LanguageOption? _language;
    [ObservableProperty] private Choice<string>? _theme;
    [ObservableProperty] private Choice<DisplayProfile>? _display;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startInTray;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _animations;
    [ObservableProperty] private Choice<CaptureMode?>? _printKey;
    [ObservableProperty] private Choice<TrayDefaultAction>? _trayAction;
    [ObservableProperty] private Choice<TrayRecordingClick>? _recordingClick;

    // ---------------------------------------------------------------- storage
    [ObservableProperty] private Choice<StorageMode>? _storage;
    [ObservableProperty] private string _imageFolder = "";
    [ObservableProperty] private string _videoFolder = "";
    [ObservableProperty] private string _commonFolder = "";
    [ObservableProperty] private Choice<int>? _retention;
    [ObservableProperty] private Choice<TrashMode>? _trashMode;
    [ObservableProperty] private Choice<TrimTarget>? _trimTarget;
    [ObservableProperty] private bool _videoLoop;
    [ObservableProperty] private Choice<SecondaryAudio>? _secondaryAudio;
    [ObservableProperty] private bool _secondaryMicrophone;

    // ---------------------------------------------------------------- capture
    [ObservableProperty] private bool _openEditorAfterCapture;
    [ObservableProperty] private bool _copyAfterCapture;
    [ObservableProperty] private bool _excludeFromHistory;
    [ObservableProperty] private bool _includeCursor;
    [ObservableProperty] private Choice<int>? _delay;
    [ObservableProperty] private Choice<CaptureScrollMode>? _scrollMode;
    [ObservableProperty] private bool _recognizeText;

    // ---------------------------------------------------------------- export / redact
    [ObservableProperty] private Choice<ImageFormat>? _imageFormat;
    [ObservableProperty] private int _jpegQuality;
    [ObservableProperty] private bool _autoExportImage;
    [ObservableProperty] private Choice<VideoFormat>? _videoFormat;
    [ObservableProperty] private Choice<RedactMode>? _redactMode;
    [ObservableProperty] private string _redactWords = "";
    [ObservableProperty] private bool _redactSystemNames;

    // ---------------------------------------------------------------- hotkeys
    [ObservableProperty] private string _hotkeyRegion = "";
    [ObservableProperty] private string _hotkeyWindow = "";
    [ObservableProperty] private string _hotkeyFullScreen = "";
    [ObservableProperty] private string _hotkeyScrolling = "";
    [ObservableProperty] private string _hotkeyDelayed = "";
    [ObservableProperty] private string _hotkeyVideo = "";
    [ObservableProperty] private string _hotkeyVideoFullScreen = "";
    [ObservableProperty] private string _hotkeyPause = "";

    // ---------------------------------------------------------------- video
    [ObservableProperty] private Choice<int>? _fps;
    [ObservableProperty] private Choice<VideoProfile>? _profile;
    [ObservableProperty] private bool _chroma444;
    [ObservableProperty] private Choice<int>? _outputHeight;
    [ObservableProperty] private bool _videoCountdown;
    [ObservableProperty] private bool _askSecondaryAudio;
    [ObservableProperty] private string _hotkeyToolbar = "";
    [ObservableProperty] private int _quality;
    [ObservableProperty] private Choice<string>? _encoder;
    [ObservableProperty] private bool _recordSystemSound;
    [ObservableProperty] private bool _recordMicrophone;
    [ObservableProperty] private bool _videoShowCursor;
    [ObservableProperty] private bool _cursorHighlight;
    [ObservableProperty] private bool _clickEffects;
    [ObservableProperty] private bool _showRecordingToolbar;
    [ObservableProperty] private bool _showRecordingFrame;
    [ObservableProperty] private bool _openGalleryAfterRecording;
    [ObservableProperty] private Choice<PlayerMode>? _player;
    [ObservableProperty] private string _customPlayer = "";
    [ObservableProperty] private bool _useGplBuild;
    [ObservableProperty] private string _ffmpegStatus = "";
    [ObservableProperty] private bool _ffmpegInstalled;
    [ObservableProperty] private bool _isTesting;

    // ---------------------------------------------------------------- updates
    [ObservableProperty] private bool _updateCheck;

    public bool IsPortable => AppEnvironment.IsPortable;

    public bool IsSeparate => Storage?.Value == StorageMode.Separate;

    public bool IsCommon => Storage?.Value == StorageMode.Common;

    public bool IsCustomPlayer => Player?.Value == PlayerMode.Custom;

    public bool IsInternalTrash => TrashMode?.Value != Services.TrashMode.Windows;

    /// <summary>Named card the view scrolls to once it is shown (e.g. "FfmpegCard" from the gallery pill).</summary>
    public string? PendingScroll { get; set; }

    /// <summary>Set by the current view.</summary>
    public Action? ScrollHandler { get; set; }

    public void ScrollTo(string name)
    {
        PendingScroll = name;
        ScrollHandler?.Invoke();
    }

    public bool IsJpeg => ImageFormat?.Value == ImageKind.Jpeg;

    public string DataFolder => AppEnvironment.DataDirectory;

    public string UpdateStatus => _updates.StatusText;

    public bool HasUpdate => _updates.Latest is not null;

    public string? HotkeyWarning
    {
        get
        {
            var failed = _hotkeys.Failed.Select(m => _settings.HotkeyText(m)).ToList();
            if (_hotkeys.PauseFailed)
            {
                failed.Add(_settings.PauseHotkey);
            }

            return failed.Count == 0 ? null : Loc.F("HkConflict", string.Join(", ", failed));
        }
    }

    public string DetectedEncoderText => string.IsNullOrEmpty(_settings.DetectedEncoder)
        ? Loc.T("EncNotTested")
        : Loc.F("EncDetected", _settings.DetectedEncoder);

    public override void OnShown()
    {
        Load();
        OnPropertyChanged(nameof(HotkeyWarning));
        OnPropertyChanged(nameof(UpdateStatus));
    }

    private void Load()
    {
        _loading = true;
        Language = LanguageOption.ForSetting(_settings.Language);
        Theme = Themes.FirstOrDefault(t => t.Value == _settings.Theme) ?? Themes[0];
        Display = Displays.First(d => d.Value == _settings.Display);
        StartWithWindows = OperatingSystem.IsWindows()
                           && (Autostart.IsMachineWide ? _settings.StartWithWindows : Autostart.IsEnabled(AppEnvironment.ExecutablePath));
        StartInTray = _settings.StartInTray;
        MinimizeToTray = _settings.MinimizeToTray;
        Animations = _settings.Animations;
        PrintKey = PrintKeyChoices.FirstOrDefault(c => c.Value == _settings.PrintKeyMode) ?? PrintKeyChoices[0];
        TrayAction = TrayActions.First(a => a.Value == _settings.TrayDefaultAction);
        RecordingClick = RecordingClicks.First(a => a.Value == _settings.TrayRecordingClick);
        Storage = StorageModes.First(m => m.Value == _settings.Storage);
        ImageFolder = _settings.EffectiveImageFolder;
        VideoFolder = _settings.EffectiveVideoFolder;
        CommonFolder = _settings.CommonRoot;
        Retention = Retentions.FirstOrDefault(r => r.Value == _settings.TrashRetentionDays) ?? Retentions[2];
        TrashMode = TrashModes.First(m => m.Value == _settings.TrashMode);
        TrimTarget = TrimTargets.First(m => m.Value == _settings.TrimTarget);
        VideoLoop = _settings.VideoLoop;
        SecondaryAudio = SecondaryAudios.First(m => m.Value == _settings.SecondaryAudio);
        SecondaryMicrophone = _settings.SecondaryMicrophone;
        OpenEditorAfterCapture = _settings.OpenEditorAfterCapture;
        CopyAfterCapture = _settings.CopyAfterCapture;
        ExcludeFromHistory = _settings.ExcludeFromClipboardHistory;
        IncludeCursor = _settings.IncludeCursor;
        Delay = Delays.FirstOrDefault(d => d.Value == _settings.DelaySeconds) ?? Delays[0];
        ScrollMode = ScrollModes.First(m => m.Value == _settings.ScrollMode);
        RecognizeText = _settings.RecognizeText;
        ImageFormat = ImageFormats.First(f => f.Value == _settings.ImageFormat);
        JpegQuality = _settings.JpegQuality;
        AutoExportImage = _settings.AutoExportImage;
        VideoFormat = VideoFormats.First(f => f.Value == _settings.VideoFormat);
        RedactMode = RedactModes.First(m => m.Value == _settings.RedactMode);
        RedactWords = string.Join(", ", _settings.RedactWords);
        RedactSystemNames = _settings.RedactSystemNames;
        HotkeyRegion = _settings.HotkeyText(CaptureMode.ImageRegion);
        HotkeyWindow = _settings.HotkeyText(CaptureMode.ImageWindow);
        HotkeyFullScreen = _settings.HotkeyText(CaptureMode.ImageFullScreen);
        HotkeyScrolling = _settings.HotkeyText(CaptureMode.ImageScrolling);
        HotkeyDelayed = _settings.HotkeyText(CaptureMode.ImageDelayed);
        HotkeyVideo = _settings.HotkeyText(CaptureMode.VideoRegion);
        HotkeyVideoFullScreen = _settings.HotkeyText(CaptureMode.VideoFullScreen);
        HotkeyPause = Hotkey.Parse(_settings.PauseHotkey).ToString();
        Fps = FpsChoices.FirstOrDefault(f => f.Value == _settings.VideoFps) ?? FpsChoices[2];
        Profile = Profiles.First(p => p.Value == _settings.VideoProfile);
        Chroma444 = _settings.Chroma444;
        OutputHeight = OutputHeights.FirstOrDefault(o => o.Value == _settings.VideoOutputHeight) ?? OutputHeights[0];
        VideoCountdown = _settings.VideoCountdown;
        AskSecondaryAudio = _settings.AskSecondaryAudio;
        HotkeyToolbar = Hotkey.Parse(_settings.ToolbarHotkey).ToString();
        Templates.Clear();
        foreach (var template in _settings.Templates)
        {
            Templates.Add(new TemplateItem(template, _settings.Save));
        }

        OnPropertyChanged(nameof(HasTemplates));
        OnPropertyChanged(nameof(HasToolbarPosition));
        Quality = _settings.VideoQuality;
        Encoder = Encoders.FirstOrDefault(e => e.Value == _settings.VideoEncoder) ?? Encoders[0];
        RecordSystemSound = _settings.RecordSystemSound;
        RecordMicrophone = _settings.RecordMicrophone;
        VideoShowCursor = _settings.VideoShowCursor;
        CursorHighlight = _settings.CursorHighlight;
        ClickEffects = _settings.ClickEffects;
        ShowRecordingToolbar = _settings.ShowRecordingToolbar;
        ShowRecordingFrame = _settings.ShowRecordingFrame;
        OpenGalleryAfterRecording = _settings.OpenGalleryAfterRecording;
        Player = Players.First(p => p.Value == _settings.Player);
        CustomPlayer = _settings.CustomPlayer ?? "";
        UseGplBuild = _settings.FfmpegVariant == FfmpegVariant.Gpl;
        UpdateCheck = _settings.UpdateCheck;
        RefreshFfmpeg();
        RaiseDerived();
        _loading = false;
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(IsSeparate));
        OnPropertyChanged(nameof(IsCommon));
        OnPropertyChanged(nameof(IsCustomPlayer));
        OnPropertyChanged(nameof(IsJpeg));
        OnPropertyChanged(nameof(IsInternalTrash));
    }

    private void Set(Action apply)
    {
        if (_loading)
        {
            return;
        }

        apply();
        _settings.Save();
    }

    // ---------------------------------------------------------------- change handlers

    partial void OnLanguageChanged(LanguageOption? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        _settings.Language = value.IsAuto ? null : value.Code;
        Loc.Apply(_settings.Language ?? Loc.Detect());
        _settings.Save();
    }

    partial void OnThemeChanged(Choice<string>? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        _settings.Theme = value.Value;
        ThemeService.Apply(value.Value);
        _settings.Save();
    }

    partial void OnDisplayChanged(Choice<DisplayProfile>? value)
    {
        if (value is not null)
        {
            Set(() => _settings.Display = value.Value);
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading || !OperatingSystem.IsWindows())
        {
            return;
        }

        _settings.StartWithWindows = value;
        if (value)
        {
            Autostart.Enable(AppEnvironment.ExecutablePath, _settings.StartInTray);
        }
        else
        {
            Autostart.Disable();
        }

        _settings.Save();
    }

    partial void OnStartInTrayChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        _settings.StartInTray = value;
        if (OperatingSystem.IsWindows() && StartWithWindows)
        {
            Autostart.Enable(AppEnvironment.ExecutablePath, value);
        }

        _settings.Save();
    }

    partial void OnMinimizeToTrayChanged(bool value) => Set(() => _settings.MinimizeToTray = value);

    partial void OnAnimationsChanged(bool value) => Set(() => _settings.Animations = value);

    partial void OnPrintKeyChanged(Choice<CaptureMode?>? value)
    {
        if (value is not null)
        {
            Set(() => _settings.PrintKeyMode = value.Value);
            OnPropertyChanged(nameof(PrintKeyBlocked));
        }
    }

    /// <summary>The Print key is set but Windows' own snipping holds it.</summary>
    public bool PrintKeyBlocked => _settings.PrintKeyMode is not null
                                   && (_hotkeys.PrintFailed || (OperatingSystem.IsWindows() && Platform.Windows.Input.PrintKey.IsUsedByWindows));

    [RelayCommand]
    private void ReleasePrintKey()
    {
        if (OperatingSystem.IsWindows() && Platform.Windows.Input.PrintKey.ReleaseFromWindows())
        {
            _host.ShowToast(Loc.T("SetPrintKeyReleased"));
        }

        OnPropertyChanged(nameof(PrintKeyBlocked));
    }

    partial void OnTrayActionChanged(Choice<TrayDefaultAction>? value)
    {
        if (value is not null)
        {
            Set(() => _settings.TrayDefaultAction = value.Value);
        }
    }

    partial void OnRecordingClickChanged(Choice<TrayRecordingClick>? value)
    {
        if (value is not null)
        {
            Set(() => _settings.TrayRecordingClick = value.Value);
        }
    }

    partial void OnStorageChanged(Choice<StorageMode>? value)
    {
        RaiseDerived();
        if (value is not null)
        {
            Set(() => _settings.Storage = value.Value);
            ImageFolder = _settings.EffectiveImageFolder;
            VideoFolder = _settings.EffectiveVideoFolder;
        }
    }

    partial void OnRetentionChanged(Choice<int>? value)
    {
        if (value is not null)
        {
            Set(() => _settings.TrashRetentionDays = value.Value);
        }
    }

    partial void OnTrashModeChanged(Choice<TrashMode>? value)
    {
        RaiseDerived();
        if (value is not null)
        {
            Set(() => _settings.TrashMode = value.Value);
        }
    }

    partial void OnTrimTargetChanged(Choice<TrimTarget>? value)
    {
        if (value is not null)
        {
            Set(() => _settings.TrimTarget = value.Value);
        }
    }

    partial void OnVideoLoopChanged(bool value) => Set(() => _settings.VideoLoop = value);

    partial void OnSecondaryAudioChanged(Choice<SecondaryAudio>? value)
    {
        if (value is not null)
        {
            Set(() => _settings.SecondaryAudio = value.Value);
        }
    }

    partial void OnSecondaryMicrophoneChanged(bool value) => Set(() => _settings.SecondaryMicrophone = value);

    partial void OnOpenEditorAfterCaptureChanged(bool value) => Set(() => _settings.OpenEditorAfterCapture = value);

    partial void OnCopyAfterCaptureChanged(bool value) => Set(() => _settings.CopyAfterCapture = value);

    partial void OnExcludeFromHistoryChanged(bool value) => Set(() => _settings.ExcludeFromClipboardHistory = value);

    partial void OnIncludeCursorChanged(bool value) => Set(() => _settings.IncludeCursor = value);

    partial void OnDelayChanged(Choice<int>? value)
    {
        if (value is not null)
        {
            Set(() => _settings.DelaySeconds = value.Value);
        }
    }

    partial void OnScrollModeChanged(Choice<CaptureScrollMode>? value)
    {
        if (value is not null)
        {
            Set(() => _settings.ScrollMode = value.Value);
        }
    }

    partial void OnRecognizeTextChanged(bool value) => Set(() => _settings.RecognizeText = value);

    partial void OnImageFormatChanged(Choice<ImageFormat>? value)
    {
        RaiseDerived();
        if (value is not null)
        {
            Set(() => _settings.ImageFormat = value.Value);
        }
    }

    partial void OnJpegQualityChanged(int value) => Set(() => _settings.JpegQuality = Math.Clamp(value, 40, 100));

    partial void OnAutoExportImageChanged(bool value) => Set(() => _settings.AutoExportImage = value);

    partial void OnVideoFormatChanged(Choice<VideoFormat>? value)
    {
        if (value is not null)
        {
            Set(() => _settings.VideoFormat = value.Value);
        }
    }

    partial void OnRedactModeChanged(Choice<RedactMode>? value)
    {
        if (value is not null)
        {
            Set(() => _settings.RedactMode = value.Value);
        }
    }

    partial void OnRedactWordsChanged(string value) => Set(() => _settings.RedactWords = value
        .Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());

    partial void OnRedactSystemNamesChanged(bool value) => Set(() => _settings.RedactSystemNames = value);

    partial void OnHotkeyRegionChanged(string value) => SetHotkey(CaptureMode.ImageRegion, value);

    partial void OnHotkeyWindowChanged(string value) => SetHotkey(CaptureMode.ImageWindow, value);

    partial void OnHotkeyFullScreenChanged(string value) => SetHotkey(CaptureMode.ImageFullScreen, value);

    partial void OnHotkeyScrollingChanged(string value) => SetHotkey(CaptureMode.ImageScrolling, value);

    partial void OnHotkeyDelayedChanged(string value) => SetHotkey(CaptureMode.ImageDelayed, value);

    partial void OnHotkeyVideoChanged(string value) => SetHotkey(CaptureMode.VideoRegion, value);

    partial void OnHotkeyVideoFullScreenChanged(string value) => SetHotkey(CaptureMode.VideoFullScreen, value);

    partial void OnHotkeyPauseChanged(string value) => Set(() => _settings.PauseHotkey = Hotkey.Parse(value).ToString());

    partial void OnProfileChanged(Choice<VideoProfile>? value)
    {
        if (value is not null)
        {
            Set(() => _settings.VideoProfile = value.Value);
        }
    }

    partial void OnChroma444Changed(bool value) => Set(() => _settings.Chroma444 = value);

    partial void OnOutputHeightChanged(Choice<int>? value)
    {
        if (value is not null)
        {
            Set(() => _settings.VideoOutputHeight = value.Value);
        }
    }

    partial void OnVideoCountdownChanged(bool value) => Set(() => _settings.VideoCountdown = value);

    partial void OnAskSecondaryAudioChanged(bool value) => Set(() => _settings.AskSecondaryAudio = value);

    partial void OnHotkeyToolbarChanged(string value) => Set(() => _settings.ToolbarHotkey = Hotkey.Parse(value).ToString());

    [RelayCommand]
    private void ResetToolbarPosition()
    {
        _settings.ToolbarX = null;
        _settings.ToolbarY = null;
        _settings.Save();
        OnPropertyChanged(nameof(HasToolbarPosition));
    }

    [RelayCommand]
    private void StartTemplate(TemplateItem? item)
    {
        if (item is not null)
        {
            _host.StartTemplate(item.Template);
        }
    }

    [RelayCommand]
    private async Task DeleteTemplateAsync(TemplateItem? item)
    {
        if (item is null || !await _host.Confirm(Loc.T("SetTemplateDelete"), item.Name, Loc.T("SetTemplateDelete")))
        {
            return;
        }

        _settings.Templates.Remove(item.Template);
        _settings.Save();
        Templates.Remove(item);
        OnPropertyChanged(nameof(HasTemplates));
    }

    partial void OnFpsChanged(Choice<int>? value)
    {
        if (value is not null)
        {
            Set(() => _settings.VideoFps = value.Value);
        }
    }

    partial void OnQualityChanged(int value) => Set(() => _settings.VideoQuality = value);

    partial void OnEncoderChanged(Choice<string>? value)
    {
        if (value is not null)
        {
            Set(() => _settings.VideoEncoder = value.Value);
        }
    }

    partial void OnRecordSystemSoundChanged(bool value) => Set(() => _settings.RecordSystemSound = value);

    partial void OnRecordMicrophoneChanged(bool value) => Set(() => _settings.RecordMicrophone = value);

    partial void OnVideoShowCursorChanged(bool value) => Set(() => _settings.VideoShowCursor = value);

    partial void OnCursorHighlightChanged(bool value) => Set(() => _settings.CursorHighlight = value);

    partial void OnClickEffectsChanged(bool value) => Set(() => _settings.ClickEffects = value);

    partial void OnShowRecordingToolbarChanged(bool value) => Set(() => _settings.ShowRecordingToolbar = value);

    partial void OnShowRecordingFrameChanged(bool value) => Set(() => _settings.ShowRecordingFrame = value);

    partial void OnOpenGalleryAfterRecordingChanged(bool value) => Set(() => _settings.OpenGalleryAfterRecording = value);

    partial void OnPlayerChanged(Choice<PlayerMode>? value)
    {
        RaiseDerived();
        if (value is not null)
        {
            Set(() => _settings.Player = value.Value);
        }
    }

    partial void OnUseGplBuildChanged(bool value) => Set(() => _settings.FfmpegVariant = value ? FfmpegVariant.Gpl : FfmpegVariant.Lgpl);

    /// <summary>Opt-in: switching on asks once (yes/no) before anything is sent to GitHub.</summary>
    partial void OnUpdateCheckChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        _ = ApplyUpdateCheckAsync(value);
    }

    /// <summary>Opt-in: nothing is sent to GitHub before the user said yes once.</summary>
    private async Task<bool> EnsureUpdateConsentAsync()
    {
        if (_settings.UpdateConsentAsked)
        {
            return true;
        }

        if (!await _host.Ask(Loc.T("UpdConsentTitle"), Loc.T("UpdConsentText"), Loc.T("UpdConsentYes")))
        {
            return false;
        }

        _settings.UpdateConsentAsked = true;
        _settings.Save();
        return true;
    }

    private async Task ApplyUpdateCheckAsync(bool value)
    {
        if (value && !await EnsureUpdateConsentAsync())
        {
            _loading = true;
            UpdateCheck = false;
            _loading = false;
            return;
        }

        _settings.UpdateCheck = value;
        _settings.Save();
        if (value)
        {
            await _updates.CheckAsync(manual: true);
        }
    }

    private void SetHotkey(CaptureMode mode, string text)
    {
        if (_loading)
        {
            return;
        }

        _settings.Hotkeys[mode] = Hotkey.Parse(text).ToString();
        _settings.Save();
    }

    // ---------------------------------------------------------------- commands

    [RelayCommand]
    private void ResetHotkeys()
    {
        _settings.Hotkeys = new Dictionary<CaptureMode, string>(UiSettings.DefaultHotkeys);
        _settings.PauseHotkey = UiSettings.DefaultPauseHotkey;
        _settings.ToolbarHotkey = UiSettings.DefaultToolbarHotkey;
        _settings.Save();
        Load();
    }

    [RelayCommand]
    private async Task BrowseFolderAsync(string which)
    {
        if (PickFolder is null || await PickFolder() is not { } folder)
        {
            return;
        }

        switch (which)
        {
            case "video":
                _settings.VideoFolder = folder;
                break;
            case "common":
                _settings.CommonFolder = folder;
                break;
            default:
                _settings.ImageFolder = folder;
                break;
        }

        _settings.CaptureFolder = null;
        _settings.Save();
        Load();
    }

    [RelayCommand]
    private void ResetFolders()
    {
        _settings.ImageFolder = null;
        _settings.VideoFolder = null;
        _settings.CommonFolder = null;
        _settings.CaptureFolder = null;
        _settings.Save();
        Load();
    }

    [RelayCommand]
    private void OpenFolder(string which) => AppEnvironment.OpenFolder(which switch
    {
        "video" => _settings.EffectiveVideoFolder,
        "common" => _settings.CommonRoot,
        _ => _settings.EffectiveImageFolder
    }, create: true);

    [RelayCommand]
    private void OpenDataFolder() => AppEnvironment.OpenFolder(AppEnvironment.DataDirectory, create: true);

    [RelayCommand]
    private async Task BrowsePlayerAsync()
    {
        if (PickProgram is null || await PickProgram() is not { } exe)
        {
            return;
        }

        CustomPlayer = exe;
        _settings.CustomPlayer = exe;
        _settings.Save();
    }

    [RelayCommand]
    private async Task InstallFfmpegAsync()
    {
        await _gate.EnsureAsync(_host.VisibleOwner());
        _settings.DetectedEncoder = null;
        _settings.Save();
        RefreshFfmpeg();
        _host.RaiseFfmpegChanged();
    }

    [RelayCommand]
    private async Task RemoveFfmpegAsync()
    {
        if (!await _host.Confirm(Loc.T("FfRemoveTitle"), Loc.T("FfRemoveText"), Loc.T("FfRemove")))
        {
            return;
        }

        _ffmpeg.Remove();
        _settings.DetectedEncoder = null;
        _settings.Save();
        RefreshFfmpeg();
        _host.RaiseFfmpegChanged();
    }

    [RelayCommand]
    private async Task TestEncodersAsync()
    {
        if (_ffmpeg.ResolveExecutable() is not { } exe)
        {
            return;
        }

        IsTesting = true;
        try
        {
            _settings.DetectedEncoder = await FfmpegProbe.BestH264EncoderAsync(exe, CancellationToken.None);
            _settings.Save();
        }
        finally
        {
            IsTesting = false;
            OnPropertyChanged(nameof(DetectedEncoderText));
        }
    }

    /// <summary>Settings › Reset SnapNova (not while recording).</summary>
    [RelayCommand]
    private async Task ResetAppAsync()
    {
        if (_host.IsRecording())
        {
            _host.ShowToast(Loc.T("ResetWhileRecording"));
            return;
        }

        var (count, bytes) = await Task.Run(() => AppReset.MediaSummary(_settings));
        var size = EditorViewModel.FormatSize(bytes);
        if (await Views.ResetWindow.ShowAsync(_host.VisibleOwner(), count, size, FfmpegInstalled) is { } choice)
        {
            await _host.ResetApp(choice);
        }
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (await EnsureUpdateConsentAsync())
        {
            await _updates.CheckAsync(manual: true);
        }
    }

    [RelayCommand]
    private Task DownloadUpdateAsync() => _updates.DownloadAsync();

    private void RefreshFfmpeg()
    {
        var state = _ffmpeg.LoadState();
        FfmpegInstalled = _ffmpeg.ResolveExecutable() is not null;
        FfmpegStatus = FfmpegInstalled && state is not null
            ? Loc.F(state.Custom ? "FfStatusCustom" : "FfStatusInstalled", state.Version, state.Executable)
            : Loc.T("FfStatusMissing");
        OnPropertyChanged(nameof(DetectedEncoderText));
    }
}
