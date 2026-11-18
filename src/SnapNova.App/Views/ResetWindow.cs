using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SnapNova.App.Controls;
using SnapNova.App.Localization;
using SnapNova.App.ViewModels;

namespace SnapNova.App.Views;

/// <summary>Result of the reset dialog.</summary>
public sealed record ResetChoice(bool DeleteMedia, bool RemoveFfmpeg);

/// <summary>
/// "Reset SnapNova": what is reset, switch "Also delete all captures" (off) with count and size, switch "Remove FFmpeg"
/// (off). With captures the main button only works after ticking "I understand". Cancel left, Reset right.
/// </summary>
public sealed class ResetWindow : Window
{
    private ResetChoice? _result;

    private ResetWindow(int mediaCount, string mediaSize, bool ffmpegInstalled)
    {
        Title = Loc.T("ResetTitle");
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://SnapNova/Assets/snapnova.ico")));
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Background = Res.Brush("BrushBgApp");
        Services.WindowChrome.Apply(this, maximize: false);

        var media = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        var ffmpeg = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center, IsEnabled = ffmpegInstalled };
        var understood = new CheckBox { Content = Loc.T("ResetUnderstood") };
        var warning = new Border
        {
            Classes = { "notice" },
            Background = new SolidColorBrush(Color.Parse("#33E53935")),
            BorderBrush = new SolidColorBrush(Color.Parse("#B0E53935")),
            IsVisible = false,
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = Loc.F("ResetMediaWarning", mediaCount, mediaSize), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = Loc.T("ResetMediaHint"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Res.Brush("BrushTextSecondary") },
                    understood
                }
            }
        };

        var cancel = new Button { Content = new IconLabel { Icon = Res.Icon("IconClose"), Text = Loc.T("BtnCancel") }, IsCancel = true };
        cancel.Classes.Add("fixed");
        cancel.Click += (_, _) => Close();
        var resetLabel = new IconLabel { Icon = Res.Icon("IconReset"), Text = Loc.T("ResetButton") };
        var reset = new Button { Content = resetLabel };
        reset.Classes.Add("dangerlabel");
        reset.Classes.Add("fixed");
        reset.Click += (_, _) =>
        {
            _result = new ResetChoice(media.IsChecked == true, ffmpeg.IsChecked == true);
            Close();
        };

        void Update()
        {
            var withMedia = media.IsChecked == true;
            warning.IsVisible = withMedia;
            resetLabel.Text = Loc.T(withMedia ? "ResetButtonMedia" : "ResetButton");
            reset.IsEnabled = !withMedia || understood.IsChecked == true;
        }

        media.IsCheckedChanged += (_, _) => Update();
        understood.IsCheckedChanged += (_, _) => Update();

        Grid Row(string text, string? hint, Control control)
        {
            var label = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
            if (hint is not null)
            {
                label.Children.Add(new TextBlock { Text = hint, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Res.Brush("BrushTextSecondary") });
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2) };
            grid.Children.Add(label);
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            return grid;
        }

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new PathIcon
        {
            Data = Res.Icon("IconReset"), Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Res.Brush("BrushAccentText")
        });
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("ResetTitle"), FontSize = 17, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("ResetText"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Foreground = Res.Brush("BrushTextSecondary")
        });
        content.Children.Add(Row(Loc.T("ResetMedia"), Loc.F("ResetMediaCount", mediaCount, mediaSize), media));
        content.Children.Add(warning);
        content.Children.Add(Row(Loc.T("ResetFfmpeg"), Loc.T("ResetFfmpegHint"), ffmpeg));
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
            Children = { cancel, reset }
        });
        Content = new Border { Padding = new Thickness(24), Child = content };
    }

    /// <returns>The choice, or null when cancelled.</returns>
    public static async Task<ResetChoice?> ShowAsync(Window? owner, int mediaCount, string mediaSize, bool ffmpegInstalled)
    {
        var window = new ResetWindow(mediaCount, mediaSize, ffmpegInstalled);
        if (owner is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            await window.ShowDialog(owner);
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }

        return window._result;
    }
}
