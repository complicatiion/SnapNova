namespace SnapNova.Core.Capture;

/// <summary>What a capture produces and how the area is chosen.</summary>
public enum CaptureMode
{
    /// <summary>Screenshot: frozen screen, region selection with window snapping (click = window, drag = region).</summary>
    ImageRegion,

    /// <summary>Screenshot of the window below the cursor (click selects, no dragging).</summary>
    ImageWindow,

    /// <summary>Screenshot of the monitor below the cursor, without overlay.</summary>
    ImageFullScreen,

    /// <summary>Video of a region or window (selected on the frozen screen, then recorded live).</summary>
    VideoRegion,

    /// <summary>Video of the monitor below the cursor.</summary>
    VideoFullScreen,

    /// <summary>Scrolling capture: region/window is scrolled (automatically or by the user) and stitched into one tall image.</summary>
    ImageScrolling,

    /// <summary>Region capture after a countdown (open menus and hover states stay in the picture).</summary>
    ImageDelayed
}

/// <summary>What a left click on the tray icon does while a video is being recorded.</summary>
public enum TrayRecordingClick
{
    Stop,
    PauseResume,
    Menu
}

/// <summary>Left click on the tray icon (Settings › General › Tray left click).</summary>
public enum TrayDefaultAction
{
    /// <summary>Small picker next to the cursor: region, window, full screen, video.</summary>
    QuickPicker,
    ImageRegion,
    VideoRegion,
    OpenGallery
}

public enum RecordingState
{
    Idle,
    Starting,
    Recording,
    Paused,
    Finishing
}

/// <summary>Starts and controls captures. The tray, hotkeys, the gallery and the command line all go through this.</summary>
public interface ICaptureCoordinator
{
    /// <summary>True while the overlay is open or a video is being recorded – a second start is ignored.</summary>
    bool IsBusy { get; }

    RecordingState Recording { get; }

    /// <summary>Raised on the UI thread whenever <see cref="IsBusy"/> or <see cref="Recording"/> changes.</summary>
    event Action? StateChanged;

    Task StartAsync(CaptureMode mode);

    Task StopRecordingAsync();

    void TogglePause();

    /// <summary>Another recording can be added (one is running, fewer than the maximum).</summary>
    bool CanAddRecording { get; }

    /// <summary>Starts an additional recording next to the running one (region selection, own colour).</summary>
    Task StartSecondaryAsync();
}

public static class CaptureModeExtensions
{
    public static bool IsVideo(this CaptureMode mode) => mode is CaptureMode.VideoRegion or CaptureMode.VideoFullScreen;

    /// <summary>Command-line value (--capture=region|window|fullscreen|video|video-fullscreen).</summary>
    public static CaptureMode? Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "region" or "image" => CaptureMode.ImageRegion,
        "window" => CaptureMode.ImageWindow,
        "fullscreen" => CaptureMode.ImageFullScreen,
        "video" or "video-region" => CaptureMode.VideoRegion,
        "video-fullscreen" => CaptureMode.VideoFullScreen,
        "scrolling" or "scroll" => CaptureMode.ImageScrolling,
        "delayed" or "timer" => CaptureMode.ImageDelayed,
        _ => null
    };
}
