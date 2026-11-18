using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SnapNova.App.Localization;
using SnapNova.App.ViewModels;

namespace SnapNova.App.Views;

/// <summary>Video editor window: draws the preview frames into a WriteableBitmap, drives the timeline.</summary>
public partial class VideoEditorWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private WriteableBitmap? _bitmap;
    private bool _dragging;

    public VideoEditorWindow()
    {
        InitializeComponent();
        Services.WindowChrome.Apply(this, maximize: false);
    }

    public VideoEditorWindow(VideoEditorViewModel vm) : this()
    {
        DataContext = vm;
        vm.PickTarget = PickTargetAsync;
        vm.Player.FrameReady += frame => Dispatcher.UIThread.Post(() => Show(vm, frame));
        _timer.Tick += (_, _) =>
        {
            if (!_dragging)
            {
                vm.Tick();
            }
        };
        _timer.Start();

        Timeline.Edit = vm.Edit;
        vm.TimelineChanged += () =>
        {
            Timeline.Position = vm.Position;
            Timeline.SelectedRemoved = vm.SelectedRemoved;
            Timeline.InvalidateVisual();
        };
        Timeline.RemovedSelected += vm.SelectRemoved;
        Timeline.EditChanged += vm.RaiseCut;
        Timeline.SeekRequested += async (time, final) =>
        {
            _dragging = !final;
            await vm.SeekAsync(time, final);
        };

        Opened += async (_, _) =>
        {
            var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
            if (screen is not null)
            {
                var area = screen.WorkingArea;
                var scale = screen.Scaling;
                Width = Math.Max(MinWidth, area.Width / scale * 0.7);
                Height = Math.Max(MinHeight, area.Height / scale * 0.75);
                Position = new PixelPoint(area.X + (int)((area.Width - Width * scale) / 2), area.Y + (int)((area.Height - Height * scale) / 2));
            }

            // Preview in native resolution, at most the pixel size of this screen (earlier: 720p – that made videos look soft).
            var maxWidth = screen?.Bounds.Width ?? 1920;
            var maxHeight = screen?.Bounds.Height ?? 1080;
            if (!await vm.OpenAsync(Math.Max(1280, maxWidth), Math.Max(720, maxHeight)))
            {
                vm.Status = Loc.T("VedOpenFailed");
            }
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            vm.Dispose();
        };
    }

    private void Show(VideoEditorViewModel vm, byte[] frame)
    {
        var w = vm.Player.FrameWidth;
        var h = vm.Player.FrameHeight;
        if (frame.Length != w * h * 4)
        {
            return;
        }

        if (_bitmap is null || _bitmap.PixelSize.Width != w || _bitmap.PixelSize.Height != h)
        {
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }

        using (var buffer = _bitmap.Lock())
        {
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(frame, y * w * 4, buffer.Address + y * buffer.RowBytes, w * 4);
            }
        }

        // A new Source reference forces Image to redraw the changed bitmap.
        Frame.Source = null;
        Frame.Source = _bitmap;
    }

    private async Task<string?> PickTargetAsync(string name, string ext)
    {
        var fileType = ext switch
        {
            ".gif" => new FilePickerFileType("GIF") { Patterns = ["*.gif"] },
            ".webm" => new FilePickerFileType("WebM") { Patterns = ["*.webm"] },
            ".mkv" => new FilePickerFileType("MKV") { Patterns = ["*.mkv"] },
            _ => new FilePickerFileType("MP4") { Patterns = ["*.mp4"] }
        };
        var vm = (VideoEditorViewModel)DataContext!;
        var folder = await StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(vm.File)!);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("VedExportTitle"),
            SuggestedFileName = name,
            DefaultExtension = ext.TrimStart('.'),
            SuggestedStartLocation = folder,
            FileTypeChoices = [fileType]
        });
        return file?.TryGetLocalPath();
    }
}
