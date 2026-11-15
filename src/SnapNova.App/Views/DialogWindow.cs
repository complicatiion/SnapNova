using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SnapNova.App.Controls;
using SnapNova.App.Localization;
using SnapNova.App.ViewModels;

namespace SnapNova.App.Views;

/// <summary>Confirmation, input and error dialog. Everything centered; Cancel left, main action right.</summary>
public sealed class DialogWindow : Window
{
    private readonly TextBox? _input;
    private bool _result;

    private DialogWindow(string title, string message, string? confirmText, bool danger, string? input, string? icon)
    {
        Title = title;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://SnapNova/Assets/snapnova.ico")));
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Background = Res.Brush("BrushBgApp");
        Services.WindowChrome.Apply(this, maximize: false);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        if (confirmText is null)
        {
            var ok = new Button { Content = new IconLabel { Icon = Res.Icon("IconCheck"), Text = "OK" }, IsDefault = true, IsCancel = true };
            ok.Classes.Add("primary");
            ok.Classes.Add("fixed");
            ok.Click += (_, _) => Close();
            buttons.Children.Add(ok);
        }
        else
        {
            var cancel = new Button { Content = new IconLabel { Icon = Res.Icon("IconClose"), Text = Loc.T("BtnCancel") }, IsCancel = true };
            cancel.Classes.Add("fixed");
            cancel.Click += (_, _) => Close();
            var confirm = new Button
            {
                Content = new IconLabel { Icon = Res.Icon(icon ?? (danger ? "IconDelete" : "IconCheck")), Text = confirmText },
                IsDefault = !danger // destructive: Enter does not confirm by accident
            };
            confirm.Classes.Add(danger ? "danger" : "primary");
            confirm.Classes.Add("fixed");
            confirm.Click += (_, _) =>
            {
                _result = true;
                Close();
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
        }

        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(new PathIcon
        {
            Data = Res.Icon(danger || confirmText is null && input is null ? "IconAlertCircle" : "IconInfo"),
            Width = 34,
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Res.Brush(danger ? "BrushDeep" : "BrushAccentText")
        });
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrEmpty(message))
        {
            content.Children.Add(new SelectableTextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Res.Brush("BrushTextSecondary")
            });
        }

        if (input is not null)
        {
            _input = new TextBox { Text = input, Width = 360, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(_input);
            Opened += (_, _) =>
            {
                _input.Focus();
                _input.SelectAll();
            };
        }

        content.Children.Add(buttons);
        Content = new Border { Padding = new Thickness(24), Child = content };
    }

    public static async Task<bool> ShowAsync(Window? owner, string title, string message, string? confirmText = null, bool danger = false,
        string? icon = null)
    {
        var dialog = new DialogWindow(title, message, confirmText, danger, null, icon);
        await ShowCoreAsync(dialog, owner);
        return dialog._result;
    }

    /// <summary>Text input (rename). Null = cancelled.</summary>
    public static async Task<string?> InputAsync(Window? owner, string title, string value, string confirmText)
    {
        var dialog = new DialogWindow(title, "", confirmText, false, value, "IconRename");
        await ShowCoreAsync(dialog, owner);
        return dialog._result ? dialog._input?.Text : null;
    }

    private static async Task ShowCoreAsync(DialogWindow dialog, Window? owner)
    {
        if (owner is { IsVisible: true })
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            await dialog.ShowDialog(owner);
            return;
        }

        var closed = new TaskCompletionSource();
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Topmost = true;
        dialog.Closed += (_, _) => closed.TrySetResult();
        dialog.Show();
        dialog.Activate();
        await closed.Task;
    }
}
