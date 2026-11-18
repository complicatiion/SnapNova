using System.Text.Json;
using System.Text.Json.Serialization;
using SnapNova.Core.Annotations;
using SnapNova.Core.Capture;
using SnapNova.Core.Media;

namespace SnapNova.App.Services;

/// <summary>One area of a multi-recording template (monitor-relative pixels).</summary>
public sealed class TemplateArea
{
    public string Monitor { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public SecondaryAudio Audio { get; set; }
}

/// <summary>Saved multi-recording: the first area is the main recording (its sound follows the video settings).</summary>
public sealed class RecordingTemplate
{
    public string Name { get; set; } = "";
    public List<TemplateArea> Areas { get; set; } = [];
}

public enum DisplayProfile
{
    Auto,
    ExtraSmall,
    Small,
    Medium,
    Large,
    ExtraLarge
}

public enum GallerySort
{
    DateDesc,
    DateAsc,
    NameAsc,
    NameDesc
}

public enum StorageMode
{
    /// <summary>Images in Pictures\SnapNova, videos in Videos\SnapNova (or two own folders).</summary>
    Separate,

    /// <summary>One folder with the subfolders "Pictures" and "Videos".</summary>
    Common
}

public enum ImageFormat
{
    Png,
    Jpeg,
    Webp
}

public enum VideoFormat
{
    Mp4H264,
    Mp4Hevc,
    Mkv
}

/// <summary>Where "Delete" puts captures.</summary>
public enum TrashMode
{
    /// <summary>Hidden .snapnova-trash folder per capture folder (works on network shares, restore inside SnapNova).</summary>
    SnapNova,

    /// <summary>Windows recycle bin (local drives only; network shares fall back to SnapNova's).</summary>
    Windows
}

/// <summary>Result of a video cut.</summary>
public enum TrimTarget
{
    /// <summary>New file with the suffix _trim next to the original (default).</summary>
    Copy,

    /// <summary>Replace the original (the original goes to the recycle bin first).</summary>
    Overwrite
}

/// <summary>Sound of an additional (second, third …) recording.</summary>
public enum SecondaryAudio
{
    None,
    Application,
    System
}

public enum PlayerMode
{
    /// <summary>The video player set as default app in Windows.</summary>
    Windows,

    /// <summary>SnapNova's preview player (video editor, needs FFmpeg).</summary>
    Internal,

    /// <summary>A program chosen by the user, for SnapNova only.</summary>
    Custom
}

public enum CaptureScrollMode
{
    Auto,
    Manual
}

/// <summary>All user settings: %LocalAppData%\SnapNova\settings.json (portable: .\data\settings.json).</summary>
public sealed class UiSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Defaults: Snagit-like but without clashing with Windows' own shortcuts.</summary>
    public static IReadOnlyDictionary<CaptureMode, string> DefaultHotkeys { get; } = new Dictionary<CaptureMode, string>
    {
        [CaptureMode.ImageRegion] = "Ctrl+Shift+S",
        [CaptureMode.ImageWindow] = "Ctrl+Shift+W",
        [CaptureMode.ImageFullScreen] = "Ctrl+Shift+F",
        [CaptureMode.ImageScrolling] = "Ctrl+Shift+L",
        [CaptureMode.ImageDelayed] = "Ctrl+Shift+D",
        [CaptureMode.VideoRegion] = "Ctrl+Shift+R",
        [CaptureMode.VideoFullScreen] = ""
    };

    public const string DefaultPauseHotkey = "Ctrl+Shift+P";
    public const string DefaultToolbarHotkey = "Ctrl+Shift+H";

    // ---------------------------------------------------------------- general
    public string? Language { get; set; }

    /// <summary>"dark" (default), "light" or "system".</summary>
    public string Theme { get; set; } = "dark";
    public DisplayProfile Display { get; set; } = DisplayProfile.Auto;
    public bool StartWithWindows { get; set; }
    public bool StartInTray { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool SetupDefaultsApplied { get; set; }
    public bool TrayHintShown { get; set; }
    public TrayDefaultAction TrayDefaultAction { get; set; } = TrayDefaultAction.QuickPicker;
    public TrayRecordingClick TrayRecordingClick { get; set; } = TrayRecordingClick.Stop;
    public bool WindowMaximized { get; set; }
    public CaptureMode? LastCaptureMode { get; set; }

    /// <summary>Smooth page and panel transitions (switch off for remote sessions or slow PCs).</summary>
    public bool Animations { get; set; } = true;

    /// <summary>Capture started by the Print key (null = Print key not used by SnapNova).</summary>
    public CaptureMode? PrintKeyMode { get; set; } = CaptureMode.ImageRegion;

    // ---------------------------------------------------------------- storage
    public StorageMode Storage { get; set; } = StorageMode.Separate;
    public string? ImageFolder { get; set; }
    public string? VideoFolder { get; set; }
    public string? CommonFolder { get; set; }

    /// <summary>Single folder of v0.0.1/v0.0.2 – taken over for images and videos when set.</summary>
    public string? CaptureFolder { get; set; }

    /// <summary>Days until SnapNova's recycle bin removes entries (0 = never).</summary>
    public int TrashRetentionDays { get; set; } = 7;
    public TrashMode TrashMode { get; set; } = TrashMode.SnapNova;

    // ---------------------------------------------------------------- capture
    public bool OpenEditorAfterCapture { get; set; } = true;
    public bool CopyAfterCapture { get; set; } = true;
    public bool ExcludeFromClipboardHistory { get; set; } = true;
    public bool IncludeCursor { get; set; }
    public int DelaySeconds { get; set; } = 3;
    public CaptureScrollMode ScrollMode { get; set; } = CaptureScrollMode.Auto;
    public bool RecognizeText { get; set; } = true;
    public Dictionary<CaptureMode, string> Hotkeys { get; set; } = new(DefaultHotkeys);
    public string PauseHotkey { get; set; } = DefaultPauseHotkey;

    /// <summary>Shows or hides the recording toolbar while recording.</summary>
    public string ToolbarHotkey { get; set; } = DefaultToolbarHotkey;

    // ---------------------------------------------------------------- export
    public ImageFormat ImageFormat { get; set; } = ImageFormat.Png;
    public int JpegQuality { get; set; } = 90;

    /// <summary>Also write the image (in <see cref="ImageFormat"/>) next to every new .snova.</summary>
    public bool AutoExportImage { get; set; }

    /// <summary>Legacy (v0.0.2): "png" or "jpg".</summary>
    public string ExportFormat { get; set; } = "png";
    public VideoFormat VideoFormat { get; set; } = VideoFormat.Mp4H264;

    // ---------------------------------------------------------------- editor / redact
    public uint EditorColor { get; set; } = 0xFFFF8C00;
    public float EditorStroke { get; set; } = 4;

    /// <summary>Background behind new text: transparent by default.</summary>
    public TextPlate EditorTextPlate { get; set; } = TextPlate.None;
    public uint EditorTextPlateColor { get; set; } = 0xFF1E1E1E;
    public RedactMode RedactMode { get; set; } = RedactMode.Blur;
    public List<string> RedactWords { get; set; } = [];
    public bool RedactSystemNames { get; set; } = true;

    // ---------------------------------------------------------------- video
    public int VideoFps { get; set; } = 30;
    /// <summary>0 … 100.</summary>
    public int VideoQuality { get; set; } = 70;
    /// <summary>"auto" or an FFmpeg encoder name (h264_nvenc, h264_qsv, h264_amf, h264_mf, libx264).</summary>
    public string VideoEncoder { get; set; } = "auto";
    public string? DetectedEncoder { get; set; }
    public bool RecordSystemSound { get; set; } = true;
    public bool RecordMicrophone { get; set; }
    public bool VideoShowCursor { get; set; } = true;
    public bool CursorHighlight { get; set; }
    public bool ClickEffects { get; set; }
    public bool ShowRecordingToolbar { get; set; } = true;
    public bool ShowRecordingFrame { get; set; } = true;
    public bool OpenGalleryAfterRecording { get; set; } = true;
    public double FollowZoom { get; set; } = 2.0;
    public FfmpegVariant FfmpegVariant { get; set; } = FfmpegVariant.Lgpl;
    /// <summary>SnapNova's player by default (falls back to the Windows player while FFmpeg is missing).</summary>
    public PlayerMode Player { get; set; } = PlayerMode.Internal;

    /// <summary>Player: clips repeat endlessly (on by default).</summary>
    public bool VideoLoop { get; set; } = true;

    /// <summary>Video cut: copy with suffix _trim (default) or overwrite.</summary>
    public TrimTarget TrimTarget { get; set; } = TrimTarget.Copy;

    /// <summary>Additional recordings (second recording): sound and microphone.</summary>
    public SecondaryAudio SecondaryAudio { get; set; } = SecondaryAudio.None;
    public bool SecondaryMicrophone { get; set; }

    /// <summary>Ask for the sound of each further recording right after its area is chosen.</summary>
    public bool AskSecondaryAudio { get; set; } = true;

    /// <summary>Saved multi-recordings: several areas/monitors started together with one click.</summary>
    public List<RecordingTemplate> Templates { get; set; } = [];

    // ---------------------------------------------------------------- video quality (0.0.8)
    public VideoProfile VideoProfile { get; set; } = VideoProfile.Screen;

    /// <summary>Full colour resolution (4:4:4): sharp coloured text; NVIDIA and CPU encoders, not every player plays it.</summary>
    public bool Chroma444 { get; set; }

    /// <summary>0 = native size; 1080 / 720 = scaled down.</summary>
    public int VideoOutputHeight { get; set; }

    /// <summary>3-2-1 before a video starts.</summary>
    public bool VideoCountdown { get; set; } = true;

    /// <summary>Last position of the recording toolbar (physical pixels); null = automatic.</summary>
    public int? ToolbarX { get; set; }
    public int? ToolbarY { get; set; }

    /// <summary>Encoder order of the detection: 2 = Media Foundation last (earlier detections are tested again once).</summary>
    public int EncoderOrderVersion { get; set; }
    public string? CustomPlayer { get; set; }

    // ---------------------------------------------------------------- gallery
    public bool GalleryList { get; set; }
    public GallerySort GallerySort { get; set; } = GallerySort.DateDesc;

    // ---------------------------------------------------------------- updates (opt-in)
    public bool UpdateCheck { get; set; }
    public bool UpdateConsentAsked { get; set; }
    public DateTimeOffset? LastUpdateCheck { get; set; }

    [JsonIgnore]
    public string EffectiveImageFolder => Storage == StorageMode.Common
        ? Path.Combine(CommonRoot, "Pictures")
        : !string.IsNullOrWhiteSpace(ImageFolder) ? ImageFolder
        : !string.IsNullOrWhiteSpace(CaptureFolder) ? CaptureFolder
        : AppEnvironment.DefaultImageFolder;

    [JsonIgnore]
    public string EffectiveVideoFolder => Storage == StorageMode.Common
        ? Path.Combine(CommonRoot, "Videos")
        : !string.IsNullOrWhiteSpace(VideoFolder) ? VideoFolder
        : !string.IsNullOrWhiteSpace(CaptureFolder) ? CaptureFolder
        : AppEnvironment.DefaultVideoFolder;

    [JsonIgnore]
    public string CommonRoot => string.IsNullOrWhiteSpace(CommonFolder) ? AppEnvironment.DefaultCommonFolder : CommonFolder;

    /// <summary>Folders of the gallery (image and video folder, once each).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> LibraryFolders =>
        new[] { EffectiveImageFolder, EffectiveVideoFolder }.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Raised after <see cref="Save"/> (tray menu, hotkeys and pages refresh).</summary>
    public event Action? Changed;

    public static string FilePath => Path.Combine(AppEnvironment.DataDirectory, "settings.json");

    public Hotkey HotkeyFor(CaptureMode mode) => Hotkeys.TryGetValue(mode, out var text) ? Hotkey.Parse(text) : default;

    public string HotkeyText(CaptureMode mode) => HotkeyFor(mode).ToString();

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), Json) ?? new UiSettings();
                foreach (var (mode, key) in DefaultHotkeys)
                {
                    settings.Hotkeys.TryAdd(mode, key);
                }

                return settings;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Damaged file: start with defaults (the file is overwritten at the next save).
        }

        return new UiSettings();
    }

    /// <summary>Set by "Reset SnapNova": no later save may bring the old settings back.</summary>
    [JsonIgnore]
    public bool Frozen { get; set; }

    public void Save()
    {
        if (Frozen)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(AppEnvironment.DataDirectory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Read-only data folder: settings apply for this session only.
        }

        Changed?.Invoke();
    }
}
