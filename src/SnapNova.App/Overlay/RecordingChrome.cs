using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SnapNova.App.Controls;
using SnapNova.App.Localization;
using SnapNova.App.ViewModels;
using SnapNova.Core.Capture;
using SnapNova.Platform.Windows.Shell;

namespace SnapNova.App.Overlay;

/// <summary>
/// Frame around the recorded area (four thin windows just OUTSIDE the area – nothing transparent, nothing click-through)
/// and the toolbar with timer, pause, stop and cancel. All of them are excluded from capture, so they never appear in
/// the video even when they overlap it.
/// </summary>
public sealed class RecordingChrome
{
    private const int Thickness = 3;

    /// <summary>Frame colours of the recordings 1–4 (red = the first, as before).</summary>
    public static readonly Color[] SessionColors =
        [Color.Parse("#E53935"), Color.Parse("#00BCD4"), Color.Parse("#E040FB"), Color.Parse("#43A047")];

    private readonly List<Window> _frame = [];
    private readonly RecordingToolbar _toolbar;
    private readonly DispatcherTimer _timer;
    private readonly bool _showToolbar;
    private readonly Color _color;

    /// <param name="addRecording">Toolbar "+" (second recording); null = no button.</param>
    public RecordingChrome(ScreenRect area, ScreenRect monitor, Func<TimeSpan> elapsed, Action togglePause, Action stop, Action cancel,
        bool showFrame = true, bool showToolbar = true, Color? color = null, Action? addRecording = null)
    {
        _showToolbar = showToolbar;
        _color = color ?? SessionColors[0];
        var t = Thickness;
        foreach (var strip in !showFrame ? Array.Empty<ScreenRect>() : new[]
                 {
                     new ScreenRect(area.X - t, area.Y - t, area.Width + 2 * t, t),
                     new ScreenRect(area.X - t, area.Bottom, area.Width + 2 * t, t),
                     new ScreenRect(area.X - t, area.Y, t, area.Height),
                     new ScreenRect(area.Right, area.Y, t, area.Height)
                 })
        {
            // Full-screen recordings: the strips would lie outside the monitor – skip those.
            if (strip.Intersect(monitor).IsEmpty)
            {
                continue;
            }

            var window = CreateStrip(strip);
            window.Background = new SolidColorBrush(_color);
            _frame.Add(window);
        }

        _toolbar = new RecordingToolbar(togglePause, stop, cancel, addRecording, _color);
        PlaceToolbar(area, monitor);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => _toolbar.SetElapsed(elapsed()));
    }

    public void Show()
    {
        foreach (var window in _frame)
        {
            window.Show();
        }

        if (_showToolbar)
        {
            _toolbar.Show();
            _timer.Start();
        }
    }

    public void SetPaused(bool paused)
    {
        IBrush brush = paused ? Brushes.Gold : new SolidColorBrush(_color);
        foreach (var window in _frame)
        {
            window.Background = brush;
        }

        _toolbar.SetPaused(paused);
    }

    public void SetBusy(string text) => _toolbar.SetBusy(text);

    /// <summary>Additional recordings in the toolbar: coloured dot with its own small stop button.</summary>
    public void AddSession(int number, Color color, Action stop) => _toolbar.AddSession(number, color, stop);

    public void RemoveSession(int number) => _toolbar.RemoveSession(number);

    public void SetCanAdd(bool canAdd) => _toolbar.SetCanAdd(canAdd);

    public void Close()
    {
        _timer.Stop();
        foreach (var window in _frame)
        {
            window.Close();
        }

        if (_showToolbar)
        {
            _toolbar.Close();
        }
    }

    internal static Window CreateStrip(ScreenRect rect)
    {
        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            CanResize = false,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,
            Background = new SolidColorBrush(Color.Parse("#E53935")),
            WindowStartupLocation = WindowStartupLocation.Manual,
            Title = "SnapNova recording frame"
        };
        Place(window, rect);
        window.Opened += (_, _) =>
        {
            Place(window, rect);
            if (window.TryGetPlatformHandle()?.Handle is { } hwnd)
            {
                WindowTools.MakeToolWindow(hwnd, noActivate: true);
                WindowTools.ExcludeFromCapture(hwnd);
            }
        };
        return window;
    }

    private void PlaceToolbar(ScreenRect area, ScreenRect monitor)
    {
        _toolbar.Opened += (_, _) =>
        {
            var scaling = _toolbar.RenderScaling;
            var width = (int)Math.Ceiling(_toolbar.ClientSize.Width * scaling);
            var height = (int)Math.Ceiling(_toolbar.ClientSize.Height * scaling);
            var x = Math.Clamp(area.X + (area.Width - width) / 2, monitor.X, Math.Max(monitor.X, monitor.Right - width));

            // Below the area, else above, else inside the bottom edge (full screen) – it is excluded from capture anyway.
            var y = area.Bottom + Thickness + 8;
            if (y + height > monitor.Bottom)
            {
                y = area.Y - Thickness - 8 - height;
            }

            if (y < monitor.Y)
            {
                y = monitor.Bottom - height - 48;
            }

            _toolbar.Position = new PixelPoint(x, y);
            if (_toolbar.TryGetPlatformHandle()?.Handle is { } hwnd)
            {
                WindowTools.MakeToolWindow(hwnd, noActivate: false);
                WindowTools.ExcludeFromCapture(hwnd);
            }
        };

        // Start near the area so the window opens on the right monitor (scaling); the exact spot follows in Opened.
        _toolbar.Position = new PixelPoint(area.X, Math.Min(area.Bottom + 8, monitor.Bottom - 80));
    }

    /// <summary>Physical rectangle → window position and size in DIPs of the target monitor.</summary>
    internal static void Place(Window window, ScreenRect rect)
    {
        var scaling = window.IsVisible && window.RenderScaling > 0
            ? window.RenderScaling
            : window.Screens.ScreenFromPoint(new PixelPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2))?.Scaling ?? 1;
        window.Position = new PixelPoint(rect.X, rect.Y);
        window.Width = Math.Max(1, rect.Width / scaling);
        window.Height = Math.Max(1, rect.Height / scaling);
    }
}

/// <summary>Small dark pill: ● 00:12  [sessions] [+] [Cancel] [Pause] [Stop].</summary>
internal sealed class RecordingToolbar : Window
{
    private readonly TextBlock _time;
    private readonly Ellipse _dot;
    private readonly Button _pause;
    private readonly IconLabel _pauseLabel;
    private readonly Button _stop;
    private readonly Button _cancel;
    private readonly Button? _add;
    private readonly StackPanel _sessions = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<int, Control> _sessionChips = [];
    private readonly Color _color;

    public RecordingToolbar(Action togglePause, Action stop, Action cancel, Action? addRecording = null, Color? color = null)
    {
        _color = color ?? RecordingChrome.SessionColors[0];
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Title = "SnapNova";

        _dot = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(_color), VerticalAlignment = VerticalAlignment.Center };
        _time = new TextBlock { Text = "00:00", FontWeight = FontWeight.SemiBold, FontSize = 14, MinWidth = 52, VerticalAlignment = VerticalAlignment.Center };
        _pauseLabel = new IconLabel { Icon = Res.Icon("IconPause"), Text = Loc.T("RecPause") };
        _pause = new Button { Content = _pauseLabel };
        _pause.Click += (_, _) => togglePause();
        _stop = new Button { Content = new IconLabel { Icon = Res.Icon("IconStop"), Text = Loc.T("RecStop") } };
        _stop.Classes.Add("primary");
        _stop.Click += (_, _) => stop();
        // Cancel: red, symbol only (tooltip names it) – clearly different from Stop, which saves.
        _cancel = new Button
        {
            Content = new PathIcon { Data = Res.Icon("IconClose"), Width = 16, Height = 16, Foreground = Brushes.White },
            Padding = new Thickness(8, 6),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _cancel.Classes.Add("cancelred");
        ToolTip.SetTip(_cancel, Loc.T("RecCancelTip"));
        _cancel.Click += (_, _) => cancel();
        if (addRecording is not null)
        {
            _add = new Button { Content = new PathIcon { Data = Res.Icon("IconVideoPlus"), Width = 16, Height = 16 } };
            ToolTip.SetTip(_add, Loc.T("RecSecondTip"));
            _add.Click += (_, _) => addRecording();
        }

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(240, 0x1E, 0x1E, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.Parse("#404040")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                // Cancel left, main action (Stop – ends all recordings) right.
                Children = { _dot, _time, _sessions }
            }
        };
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        var row = (StackPanel)((Border)Content).Child!;
        if (_add is not null)
        {
            row.Children.Add(_add);
        }

        row.Children.Add(_cancel);
        row.Children.Add(_pause);
        row.Children.Add(_stop);
    }

    public void AddSession(int number, Color color, Action stop)
    {
        var button = new Button
        {
            Padding = new Thickness(6, 2),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                    new PathIcon { Data = Res.Icon("IconStop"), Width = 12, Height = 12 }
                }
            }
        };
        ToolTip.SetTip(button, Loc.F("RecStopOne", number));
        button.Click += (_, _) => stop();
        _sessionChips[number] = button;
        _sessions.Children.Add(button);
    }

    public void RemoveSession(int number)
    {
        if (_sessionChips.Remove(number, out var chip))
        {
            _sessions.Children.Remove(chip);
        }
    }

    public void SetCanAdd(bool canAdd)
    {
        if (_add is not null)
        {
            _add.IsEnabled = canAdd;
        }
    }

    public void SetElapsed(TimeSpan elapsed) =>
        _time.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");

    public void SetPaused(bool paused)
    {
        _pauseLabel.Icon = Res.Icon(paused ? "IconPlay" : "IconPause");
        _pauseLabel.Text = Loc.T(paused ? "RecResume" : "RecPause");
        _dot.Fill = paused ? Brushes.Gold : (IBrush)new SolidColorBrush(_color);
    }

    public void SetBusy(string text)
    {
        _time.Text = text;
        _pause.IsEnabled = false;
        _stop.IsEnabled = false;
        _cancel.IsEnabled = false;
        _sessions.IsEnabled = false;
        if (_add is not null)
        {
            _add.IsEnabled = false;
        }
    }
}
