using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SnapNova.App.ViewModels;

namespace SnapNova.App.Views;

/// <summary>
/// Gallery: own multi-selection (click, Ctrl, Shift), keyboard shortcuts (Ctrl+A/C/V, Del, F2, Enter, Esc) and the width
/// report for the responsive grid. Keys typed into the search box are left alone.
/// </summary>
public partial class GalleryView : UserControl
{
    public GalleryView()
    {
        InitializeComponent();
        List.SizeChanged += (_, e) => Vm?.SetAvailableWidth(e.NewSize.Width - 18);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    private GalleryViewModel? Vm => DataContext as GalleryViewModel;

    private void OnCardLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: GalleryItemViewModel item })
        {
            Vm?.EnsureThumbnail(item);
        }
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: GalleryItemViewModel item } control || Vm is not { } vm)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);
        vm.Click(item, e.KeyModifiers.HasFlag(KeyModifiers.Control), e.KeyModifiers.HasFlag(KeyModifiers.Shift), point.Properties.IsRightButtonPressed);
        Focus();
    }

    private void OnCardDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: GalleryItemViewModel item })
        {
            Vm?.OpenCommand.Execute(item);
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.Source is TextBox)
        {
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.A when ctrl:
                vm.SelectAll();
                break;
            case Key.C when ctrl:
                _ = vm.CopyAsync(null);
                break;
            case Key.V when ctrl:
                _ = vm.PasteAsync();
                break;
            case Key.Delete:
                _ = vm.DeleteAsync(null);
                break;
            case Key.F2:
                _ = vm.RenameAsync(null);
                break;
            case Key.Enter:
                vm.Open(null);
                break;
            case Key.Escape:
                vm.ClearSelection();
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
