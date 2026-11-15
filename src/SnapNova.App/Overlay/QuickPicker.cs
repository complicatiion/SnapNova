using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SnapNova.App.Controls;
using SnapNova.App.Localization;
using SnapNova.App.ViewModels;
using SnapNova.Core.Capture;

namespace SnapNova.App.Overlay;

/// <summary>
/// Tray left click (default): a small card next to the cursor with the four captures. Closes when it loses the focus,
/// with Esc, or after a choice.
/// </summary>
public sealed class QuickPicker : Window
{
    private bool _closing;

    public QuickPicker(Action<CaptureMode> start)
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Title = "SnapNova";

        Button Choice(string icon, string text, CaptureMode mode, bool accent = false)
        {
            var button = new Button
            {
                Content = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new PathIcon { Data = Res.Icon(icon), Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = Loc.T(text), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 }
                    }
                },
                Width = 96,
                Height = 78,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            if (accent)
            {
                button.Classes.Add("primary");
            }

            button.Click += (_, _) =>
            {
                CloseOnce();
                start(mode);
            };
            return button;
        }

        Content = new Border
        {
            Background = (IBrush?)Res.Brush("BrushSurfaceCard"),
            BorderBrush = Res.Brush("BrushBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "SnapNova", FontWeight = FontWeight.Bold, Margin = new Thickness(4, 0, 0, 2) },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            Choice("IconSelection", "PickRegion", CaptureMode.ImageRegion, accent: true),
                            Choice("IconWindow", "PickWindow", CaptureMode.ImageWindow),
                            Choice("IconMonitor", "PickFullScreen", CaptureMode.ImageFullScreen),
                            Choice("IconScrollCapture", "PickScrolling", CaptureMode.ImageScrolling),
                            Choice("IconTimerCapture", "PickDelayed", CaptureMode.ImageDelayed),
                            Choice("IconVideo", "PickVideo", CaptureMode.VideoRegion)
                        }
                    }
                }
            }
        };

        Deactivated += (_, _) => CloseOnce();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseOnce();
            }
        };
    }

    /// <summary>Above-left of the cursor (the tray sits bottom right), kept inside the work area.</summary>
    public void ShowAtCursor()
    {
        var (cx, cy) = OperatingSystem.IsWindows() ? SnapNova.Platform.Windows.Capture.ScreenCapture.CursorPosition() : (0, 0);
        Opened += (_, _) =>
        {
            var scaling = RenderScaling;
            var width = (int)Math.Ceiling(ClientSize.Width * scaling);
            var height = (int)Math.Ceiling(ClientSize.Height * scaling);
            var screen = Screens.ScreenFromPoint(new PixelPoint(cx, cy)) ?? Screens.Primary;
            var area = screen?.WorkingArea ?? new PixelRect(cx - width, cy - height, width, height);
            var x = Math.Clamp(cx - width + 16, area.X, Math.Max(area.X, area.Right - width));
            var y = Math.Clamp(cy - height - 12, area.Y, Math.Max(area.Y, area.Bottom - height));
            Position = new PixelPoint(x, y);
            Activate();
        };
        Position = new PixelPoint(cx, cy);
        Show();
    }

    private void CloseOnce()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Close();
    }
}
