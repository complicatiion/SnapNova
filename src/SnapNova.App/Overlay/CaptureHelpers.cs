using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SnapNova.App.Controls;
using SnapNova.App.Localization;
using SnapNova.App.ViewModels;
using SnapNova.Core.Capture;
using SnapNova.Platform.Windows.Shell;

namespace SnapNova.App.Overlay;

/// <summary>Dark pill toolbar with a status text and two buttons (Cancel left, main action right). Excluded from capture.</summary>
public sealed class StatusToolbar : Window
{
    private readonly TextBlock _status;

    public StatusToolbar(string status, string doneText, Action done, Action cancel)
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

        _status = new TextBlock { Text = status, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 160 };
        var cancelButton = new Button { Content = new IconLabel { Icon = Res.Icon("IconClose"), Text = Loc.T("BtnCancel") } };
        cancelButton.Click += (_, _) => cancel();
        var doneButton = new Button { Content = new IconLabel { Icon = Res.Icon("IconCheck"), Text = doneText } };
        doneButton.Classes.Add("primary");
        doneButton.Click += (_, _) => done();

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(240, 0x1E, 0x1E, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.Parse("#404040")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 8),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _status, cancelButton, doneButton } }
        };

        Opened += (_, _) =>
        {
            if (TryGetPlatformHandle()?.Handle is { } hwnd)
            {
                WindowTools.MakeToolWindow(hwnd, noActivate: false);
                WindowTools.ExcludeFromCapture(hwnd);
            }
        };
    }

    public void SetStatus(string text) => _status.Text = text;

    /// <summary>Below the area (else above, else inside the bottom edge), centered.</summary>
    public void ShowNear(ScreenRect area, ScreenRect monitor)
    {
        Opened += (_, _) =>
        {
            var width = (int)Math.Ceiling(ClientSize.Width * RenderScaling);
            var height = (int)Math.Ceiling(ClientSize.Height * RenderScaling);
            var x = Math.Clamp(area.X + (area.Width - width) / 2, monitor.X, Math.Max(monitor.X, monitor.Right - width));
            var y = area.Bottom + 12;
            if (y + height > monitor.Bottom)
            {
                y = area.Y - 12 - height;
            }

            if (y < monitor.Y)
            {
                y = monitor.Bottom - height - 48;
            }

            Position = new PixelPoint(x, y);
        };
        Position = new PixelPoint(area.X, Math.Min(area.Bottom + 8, monitor.Bottom - 80));
        Show();
    }
}

/// <summary>Big countdown next to the cursor before a delayed capture (closed before the screen is frozen).</summary>
public sealed class CountdownWindow : Window
{
    private readonly TextBlock _number;

    private CountdownWindow()
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Width = 96;
        Height = 96;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Title = "SnapNova";
        _number = new TextBlock
        {
            FontSize = 44,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#1E1E1E")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Content = new Border
        {
            Width = 88,
            Height = 88,
            CornerRadius = new CornerRadius(44),
            Background = new SolidColorBrush(Color.FromArgb(235, 0xFF, 0x8C, 0x00)),
            Child = _number
        };
    }

    /// <summary>Counts down next to the cursor; false when cancelled with Esc.</summary>
    /// <param name="center">Centre of the countdown (e.g. the middle of the area about to be recorded); null = next to the cursor.</param>
    public static async Task<bool> RunAsync(int seconds, PixelPoint? center = null)
    {
        var (cx, cy) = OperatingSystem.IsWindows() ? SnapNova.Platform.Windows.Capture.ScreenCapture.CursorPosition() : (0, 0);
        var window = new CountdownWindow { Position = center is { } c ? new PixelPoint(c.X - 48, c.Y - 48) : new PixelPoint(cx + 24, cy + 24) };
        window.Opened += (_, _) =>
        {
            if (window.TryGetPlatformHandle()?.Handle is { } hwnd)
            {
                WindowTools.MakeToolWindow(hwnd, noActivate: true);
            }
        };
        window.Show();
        try
        {
            for (var i = Math.Clamp(seconds, 1, 30); i > 0; i--)
            {
                window._number.Text = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                for (var step = 0; step < 10; step++)
                {
                    await Task.Delay(100);
                    if (OperatingSystem.IsWindows() && SnapNova.Platform.Windows.Input.MouseInput.IsDown(SnapNova.Platform.Windows.Input.MouseInput.VkEscape))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        finally
        {
            window.Close();

            // The window must be gone from the screen before the freeze.
            await Task.Delay(150);
        }
    }
}

/// <summary>Frame strips around an area without toolbar (scrolling capture).</summary>
public sealed class FrameStrips(ScreenRect area, ScreenRect monitor)
{
    private readonly List<Window> _windows = [];

    public void Show(Color color)
    {
        const int t = 3;
        foreach (var strip in new[]
                 {
                     new ScreenRect(area.X - t, area.Y - t, area.Width + 2 * t, t),
                     new ScreenRect(area.X - t, area.Bottom, area.Width + 2 * t, t),
                     new ScreenRect(area.X - t, area.Y, t, area.Height),
                     new ScreenRect(area.Right, area.Y, t, area.Height)
                 })
        {
            if (strip.Intersect(monitor).IsEmpty)
            {
                continue;
            }

            var window = RecordingChrome.CreateStrip(strip);
            window.Background = new SolidColorBrush(color);
            _windows.Add(window);
            window.Show();
        }
    }

    public void Close()
    {
        foreach (var window in _windows)
        {
            window.Close();
        }

        _windows.Clear();
    }
}
