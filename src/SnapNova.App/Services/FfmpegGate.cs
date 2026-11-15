using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SnapNova.App.ViewModels;
using SnapNova.App.Views;
using SnapNova.Core.Media;

namespace SnapNova.App.Services;

/// <summary>
/// Every video feature asks here first. Screenshots never need FFmpeg.
/// No consent → license notice. Consent but no FFmpeg → download dialog with progress. Otherwise → path of ffmpeg.exe.
/// </summary>
public sealed class FfmpegGate(FfmpegProvisioner provisioner, UiSettings settings)
{
    /// <summary>FFmpeg was installed through the dialog.</summary>
    public event Action? Installed;

    public bool IsInstalled => provisioner.ResolveExecutable() is not null;

    /// <summary>ffmpeg.exe when installed (no dialog).</summary>
    public string? Executable => provisioner.ResolveExecutable();

    /// <summary>ffmpeg.exe ready to use, or null when the user declined / cancelled (the video capture is not started).</summary>
    public async Task<string?> EnsureAsync(Window? owner)
    {
        if (provisioner.ResolveExecutable() is { } ready)
        {
            return ready;
        }

        var vm = new FfmpegConsentViewModel(provisioner, settings.FfmpegVariant);
        var window = new FfmpegConsentWindow { DataContext = vm };
        vm.PickExisting = async () =>
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localization.Loc.T("FfmpegPickTitle"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("ffmpeg.exe") { Patterns = ["ffmpeg.exe"] }]
            });
            return files.Count == 1 ? files[0].TryGetLocalPath() : null;
        };

        // Without an owner (tray start) the dialog is shown centered and topmost.
        var result = owner is { IsVisible: true }
            ? await window.ShowDialog<bool>(owner)
            : await ShowStandaloneAsync(window);

        var exe = result ? provisioner.ResolveExecutable() : null;
        if (exe is not null)
        {
            Installed?.Invoke();
        }

        return exe;
    }

    private static Task<bool> ShowStandaloneAsync(FfmpegConsentWindow window)
    {
        var done = new TaskCompletionSource<bool>();
        window.Topmost = true;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Closed += (_, _) => done.TrySetResult(window.Result);
        window.Show();
        window.Activate();
        return done.Task;
    }
}
