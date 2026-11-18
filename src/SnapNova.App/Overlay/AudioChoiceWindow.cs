using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SnapNova.App.Controls;
using SnapNova.App.Localization;
using SnapNova.App.Services;
using SnapNova.App.ViewModels;
using SnapNova.Core.Capture;
using SnapNova.Platform.Windows.Shell;

namespace SnapNova.App.Overlay;

/// <summary>
/// Sound of a further recording, asked right after its area was chosen: none, the program in the area, or the whole
/// system sound. The default (Settings › Second recording) is highlighted; Enter takes it, Esc cancels the recording.
/// Excluded from capture, so it never appears in the running video.
/// </summary>
public sealed class AudioChoiceWindow : Window
{
    private readonly TaskCompletionSource<SecondaryAudio?> _result = new();

    private AudioChoiceWindow(SecondaryAudio preset, string? program)
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Title = "SnapNova";

        Button Option(SecondaryAudio value, string icon, string text)
        {
            var button = new Button
            {
                Content = new IconLabel { Icon = Res.Icon(icon), Text = text },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 8)
            };
            if (value == preset)
            {
                button.Classes.Add("primary");
            }

            button.Click += (_, _) => Finish(value);
            return button;
        }

        var programText = program is null ? Loc.T("SecAudioApp") : Loc.F("SecAudioAppNamed", program);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(245, 0x1E, 0x1E, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.Parse("#404040")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 14),
            Child = new StackPanel
            {
                Spacing = 8,
                Width = 300,
                Children =
                {
                    new IconLabel { Icon = Res.Icon("IconVolume"), Text = Loc.T("SecAudioAsk"), FontWeight = FontWeight.SemiBold },
                    Option(SecondaryAudio.None, "IconClose", Loc.T("SecAudioNone")),
                    Option(SecondaryAudio.Application, "IconWindow", programText),
                    Option(SecondaryAudio.System, "IconSpeaker", Loc.T("SecAudioSystem")),
                    new TextBlock
                    {
                        Text = Loc.T("SecAudioAskHint"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#B0B0B0"))
                    }
                }
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Finish(null);
            }
            else if (e.Key == Key.Enter)
            {
                Finish(preset);
            }
        };
        Closed += (_, _) => _result.TrySetResult(null);
    }

    private void Finish(SecondaryAudio? value)
    {
        _result.TrySetResult(value);
        Close();
    }

    /// <returns>The chosen sound, or null when cancelled (the recording is not started).</returns>
    public static Task<SecondaryAudio?> AskAsync(SecondaryAudio preset, string? program, ScreenRect area)
    {
        var window = new AudioChoiceWindow(preset, program)
        {
            Position = new PixelPoint(area.X + area.Width / 2 - 170, area.Y + area.Height / 2 - 110)
        };
        window.Opened += (_, _) =>
        {
            if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle()?.Handle is { } hwnd)
            {
                WindowTools.ExcludeFromCapture(hwnd);
            }

            window.Activate();
        };
        window.Show();
        return window._result.Task;
    }
}
