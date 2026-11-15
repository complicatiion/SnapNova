using Avalonia.Controls;
using SnapNova.App.Services;

namespace SnapNova.App.Views;

/// <summary>Gallery and settings. Closing and minimizing send it to the tray (setting), Exit is in the tray menu.</summary>
public partial class MainWindow : Window
{
    private UiSettings? _settings;

    public MainWindow()
    {
        InitializeComponent();
        WindowChrome.Apply(this, maximize: false);

        // Page change and new notification: short fade + slide (off when Settings › Animations is off).
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MainViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ViewModels.MainViewModel.CurrentPage))
                    {
                        Controls.Motion.FadeIn(PageHost, offsetY: 12, milliseconds: 200);
                    }
                    else if (e.PropertyName == nameof(ViewModels.MainViewModel.HasToast) && vm.HasToast)
                    {
                        Controls.Motion.FadeIn(ToastHost, offsetY: 16, milliseconds: 220);
                    }
                };
            }
        };

        // No fixed pixel size: the first show takes about 75 % of the work area of the current monitor.
        Opened += (_, _) =>
        {
            if (_sized || WindowState == WindowState.Maximized)
            {
                return;
            }

            _sized = true;
            if ((Screens.ScreenFromPoint(Position) ?? Screens.Primary) is { } screen)
            {
                var area = screen.WorkingArea;
                var scale = screen.Scaling;
                Width = Math.Max(MinWidth, area.Width / scale * 0.75);
                Height = Math.Max(MinHeight, area.Height / scale * 0.8);
                Position = new Avalonia.PixelPoint(area.X + (int)((area.Width - Width * scale) / 2), area.Y + (int)((area.Height - Height * scale) / 2));
            }
        };
    }

    private bool _sized;

    /// <summary>Set by App before the first show.</summary>
    public UiSettings? Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            if (value?.WindowMaximized == true)
            {
                WindowState = WindowState.Maximized;
            }
        }
    }

    /// <summary>True only when the app really exits (tray › Exit).</summary>
    public bool AllowClose { get; set; }

    public event Action? ExitRequested;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_settings is not null)
        {
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
        }

        if (AllowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_settings?.MinimizeToTray ?? true)
        {
            Hide();
        }
        else
        {
            // "Minimize to tray" off: the window button ends SnapNova.
            ExitRequested?.Invoke();
        }
    }

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && WindowState == WindowState.Minimized && (_settings?.MinimizeToTray ?? false))
        {
            Hide();
        }
    }
}
