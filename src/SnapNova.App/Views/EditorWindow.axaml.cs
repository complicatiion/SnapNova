using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SkiaSharp;
using SnapNova.App.Localization;
using SnapNova.App.ViewModels;
using SnapNova.Core.Annotations;

namespace SnapNova.App.Views;

/// <summary>Editor window: canvas, inline text input, tool shortcuts, autosave on close.</summary>
public partial class EditorWindow : Window
{
    private readonly DispatcherTimer _statusTimer;
    private TextBox? _textBox;
    private TextShape? _editing;
    private SKPoint _textAt;

    public EditorWindow()
    {
        InitializeComponent();
        Services.WindowChrome.Apply(this, maximize: false);
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            if (DataContext is EditorViewModel vm)
            {
                vm.Status = "";
            }
        };
        Surface.TextRequested += BeginTextInput;
        Surface.LayoutUpdated += (_, _) => UpdateZoomText();
        Surface.ViewChanged += UpdateZoomText;
        AddHandler(KeyDownEvent, OnToolKey, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key == Key.Space)
            {
                Surface.SpacePan = false;
            }
        }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => Surface.SpacePan = false;
    }

    public EditorWindow(EditorViewModel vm) : this()
    {
        DataContext = vm;
        Surface.ViewModel = vm;
        vm.Notify = text =>
        {
            vm.Status = text;
            _statusTimer.Stop();
            _statusTimer.Start();
        };
        vm.PickExportFile = PickExportFileAsync;
        vm.Recognize = async () => OperatingSystem.IsWindows()
            ? await SnapNova.Platform.Windows.Ocr.TextRecognizer.RecognizeAsync(Rendering.AnnotationRenderer.ToPixelImage(vm.Source))
            : null;
        vm.CopyTextToClipboard = async text =>
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        };

        // Size: about 85 % of the work area of the monitor, never larger than the screen (no fixed pixel size).
        Opened += (_, _) => FitToScreen();
        vm.CloseRequested += Close;
        vm.PropertyChanged += (_, e) =>
        {
            // Side panels slide in from the right (Settings › Animations).
            if (e.PropertyName == nameof(EditorViewModel.Panel))
            {
                Controls.Motion.FadeIn(vm.Panel == EditorPanel.Redact ? RedactPanel : vm.Panel == EditorPanel.Text ? TextPanel : null,
                    offsetX: 24, offsetY: 0, milliseconds: 200);
            }
        };
        vm.Invalidated += UpdateZoomText;

        // SnapNova never asks "save changes?": the .snova is saved when the window closes.
        Closing += (_, _) =>
        {
            CommitText();
            if (vm.IsDirty)
            {
                try
                {
                    vm.SaveNow();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Read-only location: nothing to save to.
                }
            }
        };
        Closed += (_, _) => Dispatcher.UIThread.Post(vm.Dispose, DispatcherPriority.Background);
    }

    private EditorViewModel? Vm => DataContext as EditorViewModel;

    private void FitToScreen()
    {
        if ((Screens.ScreenFromPoint(Position) ?? Screens.Primary) is not { } screen)
        {
            return;
        }

        var scaling = screen.Scaling;
        var area = screen.WorkingArea;
        Width = Math.Max(MinWidth, area.Width / scaling * 0.85);
        Height = Math.Max(MinHeight, area.Height / scaling * 0.85);
        Position = new PixelPoint(area.X + (int)((area.Width - Width * scaling) / 2), area.Y + (int)((area.Height - Height * scaling) / 2));
    }

    private void UpdateZoomText() => ZoomText.Text = $"{Surface.ViewScale * 100:0} %";

    /// <summary>Single-key tool shortcuts (V A L R E H P B T S C), Space (pan), arrows (nudge), Delete, Esc – not while typing.</summary>
    private void OnToolKey(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || _textBox is not null || e.Source is TextBox)
        {
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.D:
                    vm.DuplicateCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.OemPlus or Key.Add:
                    vm.ZoomInCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.OemMinus or Key.Subtract:
                    vm.ZoomOutCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.OemCloseBrackets:
                    vm.BringToFrontCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.OemOpenBrackets:
                    vm.SendToBackCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && vm.Selected is not null
            && (e.KeyModifiers == KeyModifiers.None || e.KeyModifiers == KeyModifiers.Shift))
        {
            var step = e.KeyModifiers == KeyModifiers.Shift ? 10 : 1;
            vm.Nudge(e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0, e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (e.Key == Key.Space)
        {
            Surface.SpacePan = true;
            e.Handled = true;
            return;
        }

        var tool = e.Key switch
        {
            Key.V => EditorTool.Select,
            Key.A => EditorTool.Arrow,
            Key.L => EditorTool.Line,
            Key.R => EditorTool.Rectangle,
            Key.E => EditorTool.Ellipse,
            Key.H => EditorTool.Highlight,
            Key.P => EditorTool.Pen,
            Key.B => EditorTool.Redact,
            Key.T => EditorTool.Text,
            Key.S => EditorTool.Step,
            Key.C => EditorTool.Crop,
            _ => (EditorTool?)null
        };
        if (tool is { } t)
        {
            vm.SelectToolCommand.Execute(t.ToString());
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Delete or Key.Back && vm.Selected is not null)
        {
            vm.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (vm.Selected is not null)
            {
                vm.Selected = null;
            }
            else if (vm.IsPanelOpen)
            {
                vm.ClosePanelCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- inline text

    private void BeginTextInput(TextShape? existing, SKPoint imagePoint, Point viewPoint)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        CommitText();
        _editing = existing;
        _textAt = imagePoint;
        var fontSize = (existing?.FontSize ?? DefaultFontSize(vm)) * Surface.ViewScale;
        _textBox = new TextBox
        {
            Text = existing?.Text ?? "",
            AcceptsReturn = true,
            FontSize = Math.Max(10, fontSize),
            FontWeight = FontWeight.Bold,
            MinWidth = 160,
            Foreground = new SolidColorBrush(Color.FromUInt32(existing?.Color ?? vm.Color)),
            Background = new SolidColorBrush(Color.FromArgb(220, 0x1E, 0x1E, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF8C00")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 2)
        };
        Canvas.SetLeft(_textBox, viewPoint.X);
        Canvas.SetTop(_textBox, viewPoint.Y);
        _textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CancelText();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                CommitText();
                e.Handled = true;
            }
        };
        _textBox.LostFocus += (_, _) => CommitText();
        Overlay.Children.Add(_textBox);
        Dispatcher.UIThread.Post(() => _textBox?.Focus(), DispatcherPriority.Input);
    }

    private static float DefaultFontSize(EditorViewModel vm) => 14 + vm.StrokeWidth * 3;

    private void CommitText()
    {
        if (_textBox is not { } box || Vm is not { } vm)
        {
            return;
        }

        _textBox = null;
        Overlay.Children.Remove(box);
        var text = (box.Text ?? "").Replace("\r\n", "\n").TrimEnd();
        if (_editing is { } shape)
        {
            vm.BeginChange();
            if (text.Length == 0)
            {
                vm.Document.Shapes.Remove(shape);
                vm.Selected = null;
            }
            else
            {
                shape.Text = text;
            }

            vm.Invalidate();
        }
        else if (text.Length > 0)
        {
            vm.AddShape(new TextShape
            {
                X = _textAt.X, Y = _textAt.Y, Text = text, FontSize = DefaultFontSize(vm), Color = vm.Color,
                Plate = vm.TextPlate, PlateColor = vm.PlateColor
            });
        }

        _editing = null;
        Surface.Focus();
    }

    private void CancelText()
    {
        if (_textBox is { } box)
        {
            _textBox = null;
            Overlay.Children.Remove(box);
        }

        _editing = null;
        Surface.Focus();
    }

    // ---------------------------------------------------------------- export

    private async Task<string?> PickExportFileAsync(string name, string extension)
    {
        var png = new FilePickerFileType("PNG") { Patterns = ["*.png"] };
        var jpg = new FilePickerFileType("JPEG") { Patterns = ["*.jpg", "*.jpeg"] };
        var webp = new FilePickerFileType("WebP") { Patterns = ["*.webp"] };
        var types = extension switch
        {
            ".jpg" => new List<FilePickerFileType> { jpg, png, webp },
            ".webp" => new List<FilePickerFileType> { webp, png, jpg },
            _ => new List<FilePickerFileType> { png, jpg, webp }
        };
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("EdExport"),
            SuggestedFileName = name,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = types,
            ShowOverwritePrompt = true
        });
        return file?.TryGetLocalPath();
    }
}
