using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SnapNova.App.Localization;
using SnapNova.App.Rendering;
using SnapNova.App.Services;
using SnapNova.Core.Annotations;
using SnapNova.Core.Capture;
using SnapNova.Core.Library;
using SnapNova.Platform.Windows.Shell;

namespace SnapNova.App.ViewModels;

public enum GalleryFilter
{
    All,
    Images,
    Videos,
    Trash
}

/// <summary>One capture in the gallery (or in the recycle bin).</summary>
public sealed partial class GalleryItemViewModel(LibraryItem item, TrashEntry? trash = null) : ObservableObject
{
    public LibraryItem Item { get; } = item;

    /// <summary>Set for entries of the recycle bin.</summary>
    public TrashEntry? Trash { get; } = trash;

    public bool IsTrash => Trash is not null;

    public string Name => Trash is null ? Item.Name : Path.GetFileNameWithoutExtension(Trash.Name);

    public string DateText => (Trash?.Deleted.LocalDateTime ?? Item.Modified).ToString("g");

    public string SizeText => Item.Size switch
    {
        >= 1024L * 1024 * 1024 => $"{Item.Size / (1024d * 1024 * 1024):0.0} GB",
        >= 1024L * 1024 => $"{Item.Size / (1024d * 1024):0.0} MB",
        _ => $"{Math.Max(1, Item.Size / 1024)} KB"
    };

    public bool IsVideo => Item.IsVideo;

    public bool IsProject => Item.Kind == LibraryItemKind.Project;

    public bool CanCopyImage => !IsVideo && !IsTrash;

    public Geometry? KindIcon => Res.Icon(Item.Kind switch
    {
        LibraryItemKind.Video => "IconVideo",
        LibraryItemKind.Project => "IconEdit",
        _ => "IconImage"
    });

    /// <summary>Type pill (top left of a card): image or video.</summary>
    public string TypeText => Loc.T(IsVideo ? "GalTypeVideo" : "GalTypeImage");

    public Geometry? TypeIcon => Res.Icon(IsVideo ? "IconVideo" : "IconImage");

    public string KindText => Item.Kind switch
    {
        LibraryItemKind.Video => Item.Extension.TrimStart('.').ToUpperInvariant(),
        LibraryItemKind.Project => "SNOVA",
        _ => Item.Extension.TrimStart('.').ToUpperInvariant()
    };

    /// <summary>Part of a multi-recording ("2/3"); empty for single captures.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChain))]
    [NotifyPropertyChangedFor(nameof(ChainTip))]
    private string _chainText = "";

    public bool HasChain => ChainText.Length > 0;

    public string ChainTip => Loc.F("GalChainTip", ChainText);

    /// <summary>Recognized text (.snova) for the search – loaded in the background.</summary>
    public string? Text { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCheck))]
    private bool _isSelected;

    /// <summary>Card checkbox visible: this card or any card is selected (otherwise only on hover).</summary>
    public bool ShowCheck => IsSelected || (Owner?.HasSelection ?? false);

    partial void OnIsSelectedChanged(bool value) => Owner?.OnItemSelectionChanged();

    internal void RaiseShowCheck() => OnPropertyChanged(nameof(ShowCheck));

    public bool HasThumbnail => Thumbnail is not null;

    public GalleryViewModel? Owner { get; init; }

    /// <summary>Tile size from the display profile (bound by the card template).</summary>
    public double TileWidth => Owner?.TileWidth ?? 248;

    public double ThumbHeight => Owner?.ThumbHeight ?? 150;

    /// <summary>List mode: preview size follows the display profile too.</summary>
    public double ListThumbWidth => Owner?.ListThumbWidth ?? 72;

    public double ListThumbHeight => Math.Round(ListThumbWidth * 0.6);

    internal bool ThumbnailRequested { get; set; }

    /// <summary>The tile size of the gallery changed.</summary>
    public void RaiseLayout()
    {
        OnPropertyChanged(nameof(TileWidth));
        OnPropertyChanged(nameof(ThumbHeight));
        OnPropertyChanged(nameof(ListThumbWidth));
        OnPropertyChanged(nameof(ListThumbHeight));
    }
}

/// <summary>A row of the virtualized grid (or one entry in list mode).</summary>
public sealed record GalleryRow(IReadOnlyList<GalleryItemViewModel> Items, bool IsList)
{
    public GalleryItemViewModel First => Items[0];
}

/// <summary>
/// Gallery: captures of the image and video folder, newest first. Rows are virtualized (thousands of captures stay
/// fast); thumbnails load when a row appears and the oldest are released again. Own multi-selection with Ctrl/Shift.
/// </summary>
public sealed partial class GalleryViewModel : PageViewModel, IDisposable
{
    private const double Gap = 14;
    private const int MaxThumbnails = 300;

    private readonly UiSettings _settings;
    private readonly ThumbnailService _thumbnails;
    private readonly AppHost _host;
    private readonly Func<CaptureMode, Task> _capture;
    private readonly DispatcherTimer _debounce;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Queue<GalleryItemViewModel> _loaded = new();
    private readonly Dictionary<string, string> _textIndex = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GalleryItemViewModel> _all = [];
    private List<GalleryItemViewModel> _visible = [];
    private GalleryItemViewModel? _anchor;
    private double _availableWidth = 1000;
    private string _watched = "";

    private bool _suppressSelection;
    private bool _hadSelection;

    /// <param name="trashPage">True: the recycle bin page of the rail (same view, fixed to the recycle bin).</param>
    public GalleryViewModel(UiSettings settings, ThumbnailService thumbnails, AppHost host, Func<CaptureMode, Task> capture, bool trashPage = false)
        : base(trashPage ? "NavTrash" : "NavGallery", trashPage ? "IconTrash" : "IconGallery")
    {
        IsTrashPage = trashPage;
        _filter = trashPage ? GalleryFilter.Trash : GalleryFilter.All;
        host.FfmpegChanged += () => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(FfmpegMissing)));
        _settings = settings;
        _thumbnails = thumbnails;
        _host = host;
        _capture = capture;
        _isList = settings.GalleryList;
        _sort = settings.GallerySort;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RefreshAsync();
        };
        _settings.Changed += OnSettingsChanged;
        WatchFolders();
    }

    public ObservableCollection<GalleryRow> Rows { get; } = [];

    public double TileWidth { get; private set; } = 248;

    public double ThumbHeight => Math.Round(TileWidth * 0.6);

    /// <summary>Preview width in list mode per display profile (Auto = M).</summary>
    public double ListThumbWidth => _settings.Display switch
    {
        DisplayProfile.ExtraSmall => 48,
        DisplayProfile.Small => 60,
        DisplayProfile.Large => 96,
        DisplayProfile.ExtraLarge => 128,
        _ => 72
    };

    [ObservableProperty]
    private GalleryFilter _filter = GalleryFilter.All;

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isList;

    [ObservableProperty]
    private GallerySort _sort;

    public bool IsEmpty => !IsLoading && _visible.Count == 0;

    public bool IsTrashView => Filter == GalleryFilter.Trash;

    public bool IsLibraryView => Filter != GalleryFilter.Trash;

    /// <summary>This instance is the recycle bin page of the rail.</summary>
    public bool IsTrashPage { get; }

    public bool IsGalleryPage => !IsTrashPage;

    public string CountText => Loc.F(IsTrashPage ? "TrashCount" : "GalCount", _visible.Count);

    public int SelectedCount => _visible.Count(i => i.IsSelected);

    public bool HasSelection => _visible.Any(i => i.IsSelected);

    public bool AllSelected => _visible.Count > 0 && _visible.All(i => i.IsSelected);

    /// <summary>Dual-use button: select all / select none.</summary>
    public Geometry? SelectAllIcon => Res.Icon(AllSelected ? "IconSelectNone" : "IconSelectAll");

    public string SelectAllTip => Loc.T(AllSelected ? "GalSelectNone" : "GalSelectAll");

    public string DeleteTip => HasSelection
        ? Loc.F(IsTrashView ? "GalDeleteForeverSel" : "GalDeleteSel", SelectedCount)
        : Loc.T("GalDeleteNone");

    /// <summary>Separate image and video folders: two folder buttons; common folder: one.</summary>
    public bool IsSeparateStorage => _settings.Storage == StorageMode.Separate;

    public bool IsCommonStorage => !IsSeparateStorage;

    /// <summary>Video recording is not possible yet: the header shows a pill that opens the FFmpeg setup.</summary>
    public bool FfmpegMissing => IsGalleryPage && !_host.FfmpegInstalled();

    public IReadOnlyList<GalleryItemViewModel> SelectedItems => _visible.Where(i => i.IsSelected).ToList();

    public Geometry? SortIcon => Res.Icon(Sort switch
    {
        GallerySort.DateAsc => "IconSortAsc",
        GallerySort.NameAsc => "IconSortAZ",
        GallerySort.NameDesc => "IconSortZA",
        _ => "IconSortDesc2"
    });

    public string SortTip => Loc.T("GalSort" + Sort);

    public bool FilterAll
    {
        get => Filter == GalleryFilter.All;
        set => SetFilter(GalleryFilter.All, value, nameof(FilterAll));
    }

    public bool FilterImages
    {
        get => Filter == GalleryFilter.Images;
        set => SetFilter(GalleryFilter.Images, value, nameof(FilterImages));
    }

    public bool FilterVideos
    {
        get => Filter == GalleryFilter.Videos;
        set => SetFilter(GalleryFilter.Videos, value, nameof(FilterVideos));
    }

    public bool FilterTrash
    {
        get => Filter == GalleryFilter.Trash;
        set => SetFilter(GalleryFilter.Trash, value, nameof(FilterTrash));
    }

    private void SetFilter(GalleryFilter filter, bool value, string property)
    {
        if (value)
        {
            Filter = filter;
        }
        else
        {
            // Clicking the active chip must not leave all chips unchecked.
            Dispatcher.UIThread.Post(() => OnPropertyChanged(property));
        }
    }

    public override void OnShown()
    {
        OnPropertyChanged(nameof(FfmpegMissing));
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void OpenFfmpegSetup() => _host.OpenFfmpegSettings();

    /// <summary>A capture was saved somewhere in the app (coordinator, editor).</summary>
    public void OnSaved(string path) => Dispatcher.UIThread.Post(() => _debounce.Start());

    /// <summary>The background OCR of a new capture finished: searchable right away.</summary>
    public void OnTextRecognized(string path, string text)
    {
        _textIndex[path] = text;
        if (_all.FirstOrDefault(i => string.Equals(i.Item.Path, path, StringComparison.OrdinalIgnoreCase)) is { } item)
        {
            item.Text = text;
        }
    }

    /// <summary>The view reports its width: columns and tile size follow (display profile).</summary>
    public void SetAvailableWidth(double width)
    {
        if (Math.Abs(width - _availableWidth) < 4 || width <= 0)
        {
            return;
        }

        _availableWidth = width;
        BuildRows();
    }

    partial void OnFilterChanged(GalleryFilter value)
    {
        OnPropertyChanged(nameof(FilterAll));
        OnPropertyChanged(nameof(FilterImages));
        OnPropertyChanged(nameof(FilterVideos));
        OnPropertyChanged(nameof(FilterTrash));
        OnPropertyChanged(nameof(IsTrashView));
        OnPropertyChanged(nameof(IsLibraryView));
        _ = RefreshAsync();
    }

    partial void OnSearchChanged(string value) => ApplyFilter();

    partial void OnIsListChanged(bool value)
    {
        _settings.GalleryList = value;
        _settings.Save();
        BuildRows();
    }

    partial void OnSortChanged(GallerySort value)
    {
        _settings.GallerySort = value;
        _settings.Save();
        OnPropertyChanged(nameof(SortIcon));
        OnPropertyChanged(nameof(SortTip));
        ApplyFilter();
    }

    private void OnSettingsChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnSettingsChanged);
            return;
        }

        WatchFolders();
        OnPropertyChanged(nameof(ListThumbWidth));
        BuildRows();
        OnPropertyChanged(nameof(IsSeparateStorage));
        OnPropertyChanged(nameof(IsCommonStorage));
    }

    [RelayCommand]
    private void ShowGrid() => IsList = false;

    [RelayCommand]
    private void ShowList() => IsList = true;

    [RelayCommand]
    private void SetSort(string sort)
    {
        if (Enum.TryParse<GallerySort>(sort, out var value))
        {
            Sort = value;
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        OnPropertyChanged(nameof(IsEmpty));
        var folders = _settings.LibraryFolders;
        var retention = _settings.TrashRetentionDays;
        var trashView = Filter == GalleryFilter.Trash;

        var items = await Task.Run(() =>
        {
            CaptureTrash.Purge(folders, retention);
            return trashView
                ? CaptureTrash.List(folders)
                    .Select(t => (Item: new LibraryItem(t.TrashPath, CaptureLibrary.KindOf(t.OriginalPath) ?? LibraryItemKind.Image, t.Deleted.LocalDateTime, t.Size), Trash: (TrashEntry?)t))
                    .ToList()
                : CaptureLibrary.Scan(folders).Select(i => (Item: i, Trash: (TrashEntry?)null)).ToList();
        });

        // Keep loaded thumbnails and the selection of unchanged files.
        var known = _all.GroupBy(i => i.Item.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _all = items.Select(i => known.TryGetValue(i.Item.Path, out var old) && old.Item == i.Item && old.IsTrash == (i.Trash is not null)
                ? old
                : new GalleryItemViewModel(i.Item, i.Trash) { Owner = this, Text = _textIndex.GetValueOrDefault(i.Item.Path) })
            .ToList();
        MarkChains(_all);
        IsLoading = false;
        ApplyFilter();
        _ = IndexTextAsync(_all.Where(i => i.IsProject && i.Text is null).ToList());
    }

    /// <summary>Videos of one multi-recording ("…", "…_rec2", "…_rec3") get a chain pill "1/3", "2/3", …</summary>
    private static void MarkChains(IReadOnlyList<GalleryItemViewModel> items)
    {
        var videos = items.Where(i => i.IsVideo && !i.IsTrash).ToList();
        var groups = new Dictionary<string, List<(GalleryItemViewModel Item, int Number)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in videos)
        {
            if (CaptureLibrary.PartOf(item.Item.Name) is { } part)
            {
                var key = Path.Combine(Path.GetDirectoryName(item.Item.Path) ?? "", part.Stem);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = [];
                    groups[key] = list;
                }

                list.Add((item, part.Number));
            }
        }

        foreach (var item in videos)
        {
            item.ChainText = "";
        }

        foreach (var item in videos)
        {
            var key = Path.Combine(Path.GetDirectoryName(item.Item.Path) ?? "", item.Item.Name);
            if (groups.TryGetValue(key, out var list) && !list.Any(e => e.Number == 1))
            {
                list.Add((item, 1));
            }
        }

        foreach (var list in groups.Values)
        {
            var ordered = list.OrderBy(e => e.Number).ThenBy(e => e.Item.Item.Name, StringComparer.OrdinalIgnoreCase).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].Item.ChainText = $"{i + 1}/{ordered.Count}";
            }
        }
    }

    /// <summary>Reads the recognized text of the .snova files once (for the search).</summary>
    private async Task IndexTextAsync(IReadOnlyList<GalleryItemViewModel> items)
    {
        foreach (var item in items)
        {
            var text = await Task.Run(() => SnovaFile.ReadDocument(item.Item.Path)?.Ocr?.FullText);
            item.Text = text ?? "";
            _textIndex[item.Item.Path] = item.Text;
        }

        if (Search.Length > 0)
        {
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        var query = Search.Trim();
        IEnumerable<GalleryItemViewModel> visible = _all.Where(i => Filter switch
        {
            GalleryFilter.Images => !i.IsVideo,
            GalleryFilter.Videos => i.IsVideo,
            _ => true
        });

        if (query.Length > 0)
        {
            visible = visible.Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                         || (i.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        visible = Sort switch
        {
            GallerySort.DateAsc => visible.OrderBy(i => i.Trash?.Deleted.LocalDateTime ?? i.Item.Modified),
            GallerySort.NameAsc => visible.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            GallerySort.NameDesc => visible.OrderByDescending(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => visible.OrderByDescending(i => i.Trash?.Deleted.LocalDateTime ?? i.Item.Modified)
        };

        _visible = visible.ToList();
        foreach (var hidden in _all.Except(_visible))
        {
            hidden.IsSelected = false;
        }

        BuildRows();
        OnPropertyChanged(nameof(IsEmpty));
        RaiseSelection();
    }

    /// <summary>
    /// Tile size from the display profile: each profile has a target width (XS 150 … XL 440 px, Auto by window width),
    /// the columns fill the whole width – so every profile looks clearly different and nothing is left empty.
    /// </summary>
    private void BuildRows()
    {
        var width = Math.Max(200, _availableWidth - 4);
        var target = _settings.Display switch
        {
            DisplayProfile.ExtraSmall => 150,
            DisplayProfile.Small => 200,
            DisplayProfile.Medium => 260,
            DisplayProfile.Large => 340,
            DisplayProfile.ExtraLarge => 440,
            _ => width < 1000 ? 220 : width < 1700 ? 260 : 300
        };

        var columns = Math.Max(1, (int)Math.Round((width + Gap) / (target + Gap)));
        var tile = Math.Floor((width - Gap * (columns - 1)) / columns);
        TileWidth = tile;
        OnPropertyChanged(nameof(ThumbHeight));
        foreach (var item in _all)
        {
            item.RaiseLayout();
        }

        Rows.Clear();
        if (IsList)
        {
            foreach (var item in _visible)
            {
                Rows.Add(new GalleryRow([item], true));
            }
        }
        else
        {
            for (var i = 0; i < _visible.Count; i += columns)
            {
                Rows.Add(new GalleryRow(_visible.Skip(i).Take(columns).ToList(), false));
            }
        }

        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>Called when a card becomes visible: load the thumbnail, release the oldest beyond the limit.</summary>
    public void EnsureThumbnail(GalleryItemViewModel item)
    {
        if (item.ThumbnailRequested)
        {
            return;
        }

        item.ThumbnailRequested = true;
        _ = LoadThumbnailAsync(item);
    }

    private async Task LoadThumbnailAsync(GalleryItemViewModel item)
    {
        var bitmap = await _thumbnails.LoadAsync(item.Item);
        if (bitmap is null)
        {
            return;
        }

        item.Thumbnail = bitmap;
        _loaded.Enqueue(item);
        while (_loaded.Count > MaxThumbnails && _loaded.Dequeue() is { } old)
        {
            old.Thumbnail = null;
            old.ThumbnailRequested = false;
        }
    }

    // ---------------------------------------------------------------- selection

    /// <summary>Click on a card: plain = only this, Ctrl = toggle, Shift = range from the anchor.</summary>
    public void Click(GalleryItemViewModel item, bool ctrl, bool shift, bool rightButton = false)
    {
        if (rightButton)
        {
            if (!item.IsSelected)
            {
                SelectOnly(item);
            }

            return;
        }

        if (shift && _anchor is not null)
        {
            var a = _visible.IndexOf(_anchor);
            var b = _visible.IndexOf(item);
            if (a >= 0 && b >= 0)
            {
                if (!ctrl)
                {
                    foreach (var i in _visible)
                    {
                        i.IsSelected = false;
                    }
                }

                for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                {
                    _visible[i].IsSelected = true;
                }

                RaiseSelection();
                return;
            }
        }

        if (ctrl)
        {
            item.IsSelected = !item.IsSelected;
            _anchor = item;
            RaiseSelection();
            return;
        }

        SelectOnly(item);
    }

    private void SelectOnly(GalleryItemViewModel item)
    {
        foreach (var i in _visible)
        {
            i.IsSelected = ReferenceEquals(i, item);
        }

        _anchor = item;
        RaiseSelection();
    }

    [RelayCommand]
    public void SelectAll() => SetAll(true);

    [RelayCommand]
    public void ClearSelection() => SetAll(false);

    [RelayCommand]
    private void ToggleSelectAll() => SetAll(!AllSelected);

    private void SetAll(bool selected)
    {
        _suppressSelection = true;
        foreach (var i in _visible)
        {
            i.IsSelected = selected;
        }

        _suppressSelection = false;
        RaiseSelection();
    }

    /// <summary>A card checkbox changed the selection.</summary>
    internal void OnItemSelectionChanged()
    {
        if (!_suppressSelection)
        {
            RaiseSelection();
        }
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(SelectAllIcon));
        OnPropertyChanged(nameof(SelectAllTip));
        OnPropertyChanged(nameof(DeleteTip));
        DeleteSelectedCommand.NotifyCanExecuteChanged();

        // Checkboxes appear on all cards as soon as something is selected.
        var has = HasSelection;
        if (has != _hadSelection)
        {
            _hadSelection = has;
            foreach (var i in _visible)
            {
                i.RaiseShowCheck();
            }
        }
    }

    /// <summary>Topbar delete button (disabled without selection): recycle bin, or permanently on the recycle bin page.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteSelectedAsync() => DeleteAsync(null);

    private IReadOnlyList<GalleryItemViewModel> Targets(GalleryItemViewModel? item) =>
        item is not null && !item.IsSelected ? [item] : SelectedItems;

    // ---------------------------------------------------------------- actions

    [RelayCommand]
    public void Open(GalleryItemViewModel? item)
    {
        item ??= SelectedItems.FirstOrDefault();
        if (item is null || item.IsTrash)
        {
            return;
        }

        if (item.IsVideo)
        {
            _host.PlayVideo(item.Item.Path);
        }
        else
        {
            _host.OpenEditor(item.Item.Path);
        }
    }

    [RelayCommand]
    private void EditVideo(GalleryItemViewModel? item)
    {
        if ((item ?? SelectedItems.FirstOrDefault()) is { IsVideo: true, IsTrash: false } video)
        {
            _host.EditVideo(video.Item.Path);
        }
    }

    [RelayCommand]
    private void ShowInFolder(GalleryItemViewModel? item) =>
        AppEnvironment.OpenFolder((item ?? SelectedItems.FirstOrDefault())?.Item.Path);

    [RelayCommand]
    private void OpenImageFolder() => AppEnvironment.OpenFolder(_settings.EffectiveImageFolder, create: true);

    [RelayCommand]
    private void OpenVideoFolder() => AppEnvironment.OpenFolder(_settings.EffectiveVideoFolder, create: true);

    [RelayCommand]
    private void OpenFolder() => AppEnvironment.OpenFolder(_settings.CommonRoot, create: true);

    /// <summary>Ctrl+C: one image as image (flattened), several entries or videos as files.</summary>
    [RelayCommand]
    public async Task CopyAsync(GalleryItemViewModel? item)
    {
        var targets = Targets(item).Where(i => !i.IsTrash).ToList();
        if (targets.Count == 0 || !OperatingSystem.IsWindows())
        {
            return;
        }

        var exclude = _settings.ExcludeFromClipboardHistory;
        var ok = await Task.Run(() =>
        {
            if (targets is [{ IsVideo: false } single])
            {
                using var flat = single.IsProject ? FlattenProject(single.Item.Path) : AnnotationRenderer.Decode(File.ReadAllBytes(single.Item.Path));
                var png = AnnotationRenderer.Encode(flat, SkiaSharp.SKEncodedImageFormat.Png);
                return ClipboardImage.Set(AnnotationRenderer.ToPixelImage(flat), png, exclude);
            }

            return ClipboardImage.SetFiles(targets.Select(t => t.Item.Path).ToList(), exclude);
        });
        _host.ShowToast(ok ? (targets.Count == 1 ? Loc.T("EdCopied") : Loc.F("GalCopiedFiles", targets.Count)) : Loc.T("EdCopyFailed"));
    }

    private static SkiaSharp.SKBitmap FlattenProject(string path)
    {
        var (png, document) = SnovaFile.Load(path);
        using var source = AnnotationRenderer.Decode(png);
        return AnnotationRenderer.Flatten(source, document);
    }

    /// <summary>Ctrl+V: image or image/video files from the clipboard become new captures.</summary>
    [RelayCommand]
    public async Task PasteAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var imported = await Task.Run(() => LibraryImport.FromClipboard(_settings));
        _host.ShowToast(imported == 0 ? Loc.T("GalPasteNothing") : Loc.F("GalPasted", imported));
        if (imported > 0)
        {
            await RefreshAsync();
        }
    }

    /// <summary>Delete: into SnapNova's recycle bin (no question – it can be restored).</summary>
    [RelayCommand]
    public async Task DeleteAsync(GalleryItemViewModel? item)
    {
        var targets = Targets(item);
        if (targets.Count == 0)
        {
            return;
        }

        if (targets[0].IsTrash)
        {
            await DeleteForeverAsync(item);
            return;
        }

        var useWindows = _settings.TrashMode == TrashMode.Windows;
        var moved = await Task.Run(() =>
        {
            var count = 0;
            foreach (var target in targets)
            {
                try
                {
                    // Windows recycle bin only for local drives – on network shares Windows would delete permanently.
                    if (!useWindows || !OperatingSystem.IsWindows() || !RecycleBin.TrySend(target.Item.Path))
                    {
                        CaptureTrash.MoveToTrash(target.Item.Path);
                    }

                    var track = CursorTrack.PathFor(target.Item.Path);
                    if (File.Exists(track))
                    {
                        File.Delete(track);
                    }

                    count++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }

            return count;
        });

        _host.ShowToast(Loc.F("GalMovedToTrash", moved));
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RestoreAsync(GalleryItemViewModel? item)
    {
        var targets = Targets(item).Where(i => i.Trash is not null).ToList();
        await Task.Run(() =>
        {
            foreach (var target in targets)
            {
                try
                {
                    CaptureTrash.Restore(target.Trash!);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        });
        _host.ShowToast(Loc.F("GalRestored", targets.Count));
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DeleteForeverAsync(GalleryItemViewModel? item)
    {
        var targets = Targets(item).Where(i => i.Trash is not null).ToList();
        if (targets.Count == 0
            || !await _host.Confirm(Loc.T("GalDeleteForeverTitle"), Loc.F("GalDeleteForeverText", targets.Count), Loc.T("GalDeleteForever")))
        {
            return;
        }

        await Task.Run(() =>
        {
            foreach (var target in targets)
            {
                try
                {
                    CaptureTrash.DeleteForever(target.Trash!);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        });
        await RefreshAsync();
    }

    /// <summary>Empties the recycle bin (header button, rail context menu) – read from disk, so it works from any page.</summary>
    [RelayCommand]
    public async Task EmptyTrashAsync()
    {
        var folders = _settings.LibraryFolders;
        var entries = await Task.Run(() => CaptureTrash.List(folders));
        if (entries.Count == 0)
        {
            _host.ShowToast(Loc.T("TrashEmptyAlready"));
            return;
        }

        if (!await _host.Confirm(Loc.T("GalEmptyTrashTitle"), Loc.F("GalDeleteForeverText", entries.Count), Loc.T("GalEmptyTrash")))
        {
            return;
        }

        await Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                try
                {
                    CaptureTrash.DeleteForever(entry);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        });
        await RefreshAsync();
    }

    /// <summary>F2: rename one capture (the mouse track of a video goes with it).</summary>
    [RelayCommand]
    public async Task RenameAsync(GalleryItemViewModel? item)
    {
        var target = item ?? (SelectedItems.Count == 1 ? SelectedItems[0] : null);
        if (target is null || target.IsTrash)
        {
            return;
        }

        var name = await _host.Input(Loc.T("GalRenameTitle"), target.Name, Loc.T("GalRename"));
        if (string.IsNullOrWhiteSpace(name) || name == target.Name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return;
        }

        try
        {
            var path = Path.Combine(Path.GetDirectoryName(target.Item.Path)!, name.Trim() + target.Item.Extension);
            File.Move(target.Item.Path, path);
            var track = CursorTrack.PathFor(target.Item.Path);
            if (File.Exists(track))
            {
                File.Move(track, CursorTrack.PathFor(path));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _host.ShowError(Loc.T("GalRenameTitle"), e.Message);
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private Task CaptureImage() => _capture(CaptureMode.ImageRegion);

    [RelayCommand]
    private Task CaptureVideo() => _capture(CaptureMode.VideoRegion);

    private void WatchFolders()
    {
        var folders = _settings.LibraryFolders;
        var key = string.Join('|', folders);
        if (key == _watched)
        {
            return;
        }

        _watched = key;
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
        foreach (var folder in folders)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, EnableRaisingEvents = true };
                watcher.Created += (_, _) => Dispatcher.UIThread.Post(() => _debounce.Start());
                watcher.Deleted += (_, _) => Dispatcher.UIThread.Post(() => _debounce.Start());
                watcher.Renamed += (_, _) => Dispatcher.UIThread.Post(() => _debounce.Start());
                _watchers.Add(watcher);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Folder not reachable (network drive offline): manual refresh still works.
            }
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
    }
}
