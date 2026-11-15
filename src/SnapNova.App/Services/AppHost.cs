using Avalonia.Controls;
using SnapNova.Core.Ocr;

namespace SnapNova.App.Services;

/// <summary>Window handling in one place (implemented by App): services ask for windows instead of creating them.</summary>
public sealed class AppHost
{
    public Action ShowGallery { get; set; } = () => { };

    public Action ShowSettings { get; set; } = () => { };

    public Action ShowAbout { get; set; } = () => { };

    public Action<string> OpenEditor { get; set; } = _ => { };

    /// <summary>Short notice: in the main window when it is open, otherwise as tray tooltip.</summary>
    public Action<string> ShowToast { get; set; } = _ => { };

    public Action<string, string> ShowError { get; set; } = (_, _) => { };

    /// <summary>Confirmation (title, text, confirm button text) → true when confirmed.</summary>
    public Func<string, string, string, Task<bool>> Confirm { get; set; } = (_, _, _) => Task.FromResult(false);

    /// <summary>Yes/no question (title, text, confirm button text) → true when confirmed (not destructive).</summary>
    public Func<string, string, string, Task<bool>> Ask { get; set; } = (_, _, _) => Task.FromResult(false);

    /// <summary>Text input (title, value, confirm button text) → null when cancelled.</summary>
    public Func<string, string, string, Task<string?>> Input { get; set; } = (_, _, _) => Task.FromResult<string?>(null);

    /// <summary>The background text recognition of a capture finished (open editor, gallery search).</summary>
    public Action<string, OcrResult> TextRecognized { get; set; } = (_, _) => { };

    /// <summary>Opens a video in the player chosen in the settings.</summary>
    public Action<string> PlayVideo { get; set; } = _ => { };

    /// <summary>Opens the video editor (trim, GIF, follow the mouse).</summary>
    public Action<string> EditVideo { get; set; } = _ => { };

    /// <summary>The main window if it is visible (owner for dialogs), else null.</summary>
    public Func<Window?> VisibleOwner { get; set; } = () => null;

    /// <summary>FFmpeg is installed (video recording possible).</summary>
    public Func<bool> FfmpegInstalled { get; set; } = () => false;

    /// <summary>Opens Settings › Video at the FFmpeg card.</summary>
    public Action OpenFfmpegSettings { get; set; } = () => { };

    /// <summary>FFmpeg was installed or removed (gallery pill, settings).</summary>
    public event Action? FfmpegChanged;

    public void RaiseFfmpegChanged() => FfmpegChanged?.Invoke();
}
