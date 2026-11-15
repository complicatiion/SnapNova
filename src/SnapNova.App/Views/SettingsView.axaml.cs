using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SnapNova.App.Localization;
using SnapNova.App.ViewModels;

namespace SnapNova.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.PickFolder = PickFolderAsync;
                vm.PickProgram = PickProgramAsync;
                vm.ScrollHandler = () => ScrollToPending(vm);
                ScrollToPending(vm);
            }
        };
    }

    private void ScrollToPending(SettingsViewModel vm) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (vm.PendingScroll is { } name && this.FindControl<Control>(name) is { } target)
            {
                vm.PendingScroll = null;
                target.BringIntoView();
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);

    private async Task<string?> PickFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("SetImageFolder"), AllowMultiple = false });
        return folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickProgramAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("SetPlayerPick"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Programs") { Patterns = ["*.exe"] }]
        });
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }
}
