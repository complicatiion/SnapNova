using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using SnapNova.App.Localization;
using SnapNova.App.Rendering;
using SnapNova.App.Services;
using SnapNova.Core.Annotations;
using SnapNova.Core.Ocr;
using SnapNova.Core.Redact;
using SnapNova.Platform.Windows.Shell;

namespace SnapNova.App.ViewModels;

public enum EditorTool
{
    Select,
    Arrow,
    Line,
    Rectangle,
    Ellipse,
    Highlight,
    Pen,
    Redact,
    Text,
    Step,
    Crop
}

public sealed record ToolOption(EditorTool Tool, Geometry? Icon, string Tip, string Shortcut);

public sealed record ColorOption(uint Value, IBrush Brush, string Name);

/// <summary>Right side panel of the editor (one at a time).</summary>
public enum EditorPanel
{
    None,
    Redact,
    Text
}

/// <summary>
/// Editor state: the original image, the annotation document, the active tool and undo/redo (JSON snapshots).
/// Saving writes the .snova (original stays untouched); export and copy burn the annotations in.
/// </summary>
public sealed partial class EditorViewModel : ObservableObject, IDisposable
{
    private readonly UiSettings _settings;
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private readonly byte[] _originalPng;

    private EditorViewModel(UiSettings settings, string path, byte[] originalPng, SKBitmap source, AnnotationDocument document)
    {
        _settings = settings;
        _originalPng = originalPng;
        FilePath = path;
        Source = source;
        Document = document;
        _color = settings.EditorColor;
        _strokeWidth = Math.Clamp(settings.EditorStroke, 1, 24);
        _selectedTool = Tools.First(t => t.Tool == EditorTool.Arrow);
        _selectedColor = Colors.FirstOrDefault(c => c.Value == settings.EditorColor) ?? Colors[0];
        _textPlate = settings.EditorTextPlate;
        _plateColor = settings.EditorTextPlateColor;
    }

    /// <summary>Opens a .snova or a plain image (PNG/JPG/GIF/BMP – saved as .snova next to it on first save).</summary>
    public static EditorViewModel Open(UiSettings settings, string path)
    {
        if (string.Equals(Path.GetExtension(path), SnovaFile.Extension, StringComparison.OrdinalIgnoreCase))
        {
            var (png, document) = SnovaFile.Load(path);
            return new EditorViewModel(settings, path, png, AnnotationRenderer.Decode(png), document);
        }

        var bytes = File.ReadAllBytes(path);
        var bitmap = AnnotationRenderer.Decode(bytes);
        var original = AnnotationRenderer.Encode(bitmap, SKEncodedImageFormat.Png);
        return new EditorViewModel(settings, Path.ChangeExtension(path, SnovaFile.Extension), original, bitmap, new AnnotationDocument());
    }

    /// <summary>Redraw request for the canvas (document, preview, selection or crop changed).</summary>
    public event Action? Invalidated;

    /// <summary>Raised to close the window after a save-and-close.</summary>
    public event Action? CloseRequested;

    /// <summary>Set by the window: file picker for the export.</summary>
    public Func<string, string, Task<string?>>? PickExportFile { get; set; }

    /// <summary>Set by the window: notice in the status line.</summary>
    public Action<string>? Notify { get; set; }

    public string FilePath { get; private set; }

    public SKBitmap Source { get; }

    public AnnotationDocument Document { get; private set; }

    public string Title => "SnapNova – " + Path.GetFileNameWithoutExtension(FilePath);

    public string SizeText => Document.Crop is { IsEmpty: false } c
        ? $"{c.Width} × {c.Height} px ({Loc.T("EdCropped")})"
        : $"{Source.Width} × {Source.Height} px";

    /// <summary>Status line: capture time (or file time) and file size – the window title of the origin only as tooltip.</summary>
    public string DateText
    {
        get
        {
            var time = Document.Info?.Created.LocalDateTime ?? (File.Exists(FilePath) ? File.GetLastWriteTime(FilePath) : DateTime.Now);
            return time.ToString("g");
        }
    }

    public string FileSizeText => File.Exists(FilePath) ? FormatSize(new FileInfo(FilePath).Length) : "–";

    public string? SourceTip => Document.Info is { } info
        ? string.Join(" · ", new[] { info.SourceApp, info.WindowTitle }.Where(s => !string.IsNullOrWhiteSpace(s)))
        : null;

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB"
    };

    public ObservableCollection<ToolOption> Tools { get; } =
    [
        new(EditorTool.Select, Res.Icon("IconCursorMove"), Loc.T("ToolSelect"), "V"),
        new(EditorTool.Arrow, Res.Icon("IconArrowDiag"), Loc.T("ToolArrow"), "A"),
        new(EditorTool.Line, Res.Icon("IconLine"), Loc.T("ToolLine"), "L"),
        new(EditorTool.Rectangle, Res.Icon("IconRectangle"), Loc.T("ToolRectangle"), "R"),
        new(EditorTool.Ellipse, Res.Icon("IconEllipse"), Loc.T("ToolEllipse"), "E"),
        new(EditorTool.Highlight, Res.Icon("IconMarker"), Loc.T("ToolHighlight"), "H"),
        new(EditorTool.Pen, Res.Icon("IconPencil"), Loc.T("ToolPen"), "P"),
        new(EditorTool.Redact, Res.Icon("IconBlur"), Loc.T("ToolRedact"), "B"),
        new(EditorTool.Text, Res.Icon("IconText"), Loc.T("ToolText"), "T"),
        new(EditorTool.Step, Res.Icon("IconStep"), Loc.T("ToolStep"), "S"),
        new(EditorTool.Crop, Res.Icon("IconCrop"), Loc.T("ToolCrop"), "C")
    ];

    public ObservableCollection<ColorOption> Colors { get; } =
    [
        Swatch(0xFFFF8C00, "Orange"),
        Swatch(0xFFE53935, "Red"),
        Swatch(0xFFFDD835, "Yellow"),
        Swatch(0xFF43A047, "Green"),
        Swatch(0xFF1E88E5, "Blue"),
        Swatch(0xFF8E24AA, "Purple"),
        Swatch(0xFFFFFFFF, "White"),
        Swatch(0xFF1E1E1E, "Black")
    ];

    [ObservableProperty]
    private ToolOption _selectedTool;

    [ObservableProperty]
    private ColorOption _selectedColor;

    [ObservableProperty]
    private uint _color;

    [ObservableProperty]
    private float _strokeWidth;

    /// <summary>0 = fit to window (never enlarged), otherwise the zoom factor.</summary>
    [ObservableProperty]
    private double _zoom;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private Shape? _selected;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _status = "";

    public EditorTool Tool => SelectedTool.Tool;

    // ---------------------------------------------------------------- text background

    private bool _loadingPlate;

    /// <summary>Text options in the toolbar: text tool active or a text selected.</summary>
    public bool ShowTextOptions => Tool == EditorTool.Text || Selected is TextShape;

    /// <summary>Background of new texts and of the selected text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlateIsNone), nameof(PlateIsBlack), nameof(PlateIsWhite), nameof(PlateIsCustom), nameof(PlateBrush))]
    private TextPlate _textPlate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlateBrush))]
    private uint _plateColor;

    public bool PlateIsNone => TextPlate == TextPlate.None;

    public bool PlateIsBlack => TextPlate == TextPlate.Black;

    public bool PlateIsWhite => TextPlate == TextPlate.White;

    public bool PlateIsCustom => TextPlate == TextPlate.Custom;

    /// <summary>Preview in the toolbar button (transparent = chequered look via the button style).</summary>
    public IBrush PlateBrush => TextPlate switch
    {
        TextPlate.Black => Brushes.Black,
        TextPlate.White => Brushes.White,
        TextPlate.Custom => new SolidColorBrush(Avalonia.Media.Color.FromUInt32(PlateColor)),
        _ => Brushes.Transparent
    };

    [RelayCommand]
    private void SetPlate(string plate)
    {
        if (Enum.TryParse<TextPlate>(plate, out var value))
        {
            TextPlate = value;
        }
    }

    [RelayCommand]
    private void SetPlateColor(ColorOption? color)
    {
        if (color is null)
        {
            return;
        }

        PlateColor = color.Value;
        TextPlate = TextPlate.Custom;
        ApplyPlate();
    }

    partial void OnTextPlateChanged(TextPlate value) => ApplyPlate();

    private void ApplyPlate()
    {
        if (_loadingPlate)
        {
            return;
        }

        _settings.EditorTextPlate = TextPlate;
        _settings.EditorTextPlateColor = PlateColor;
        if (Selected is TextShape text && (text.Plate != TextPlate || text.PlateColor != PlateColor || text.Background))
        {
            BeginChange();
            text.Plate = TextPlate;
            text.PlateColor = PlateColor;
            text.Background = false;
            Invalidate();
        }
    }

    public RedactMode RedactMode => _settings.RedactMode;

    /// <summary>Smart Redact suggestions (checked = will be hidden).</summary>
    public ObservableCollection<RedactSuggestion> Suggestions { get; } = [];

    /// <summary>Open right panel: Smart Redact or recognized text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRedactPanelOpen), nameof(IsTextPanelOpen), nameof(IsPanelOpen))]
    private EditorPanel _panel;

    public bool IsRedactPanelOpen => Panel == EditorPanel.Redact;

    public bool IsTextPanelOpen => Panel == EditorPanel.Text;

    public bool IsPanelOpen => Panel != EditorPanel.None;

    /// <summary>Recognized text for the text panel (selectable).</summary>
    [ObservableProperty]
    private string _recognizedText = "";

    /// <summary>Canvas: zoom one step in (+1) or out (-1) around the view centre.</summary>
    public event Action<int>? ZoomStepRequested;

    /// <summary>Canvas: centre the image again (fit / 100 %).</summary>
    public event Action? ViewResetRequested;

    [ObservableProperty]
    private bool _isRecognizing;

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>Set by the window: runs the text recognition on the original image.</summary>
    public Func<Task<OcrResult?>>? Recognize { get; set; }

    /// <summary>Set by the window: puts text on the clipboard.</summary>
    public Func<string, Task>? CopyTextToClipboard { get; set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public bool HasCrop => Document.Crop is { IsEmpty: false };

    /// <summary>Preview of the shape being drawn (not yet in the document).</summary>
    public Shape? Preview { get; set; }

    /// <summary>Crop rectangle being dragged.</summary>
    public SKRectI? CropPreview { get; set; }

    public void Invalidate() => Invalidated?.Invoke();

    /// <summary>Call BEFORE every change: snapshot for undo.</summary>
    public void BeginChange()
    {
        _undo.Push(Document.ToJson());
        _redo.Clear();
        IsDirty = true;
        RaiseHistory();
    }

    public void AddShape(Shape shape)
    {
        BeginChange();
        Document.Shapes.Add(shape);
        Selected = shape;
        Invalidate();
    }

    public void SetCrop(Core.Capture.ScreenRect? crop)
    {
        BeginChange();
        Document.Crop = crop;
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(HasCrop));
        Invalidate();
    }

    public int TakeStepNumber() => Document.NextStep++;

    partial void OnSelectedToolChanged(ToolOption value)
    {
        OnPropertyChanged(nameof(Tool));
        OnPropertyChanged(nameof(ShowTextOptions));
        if (value.Tool != EditorTool.Select)
        {
            Selected = null;
        }

        Invalidate();
    }

    partial void OnSelectedColorChanged(ColorOption value) => Color = value.Value;

    partial void OnColorChanged(uint value)
    {
        _settings.EditorColor = value;
        if (Selected is { } shape)
        {
            BeginChange();
            shape.Color = value;
            Invalidate();
        }
    }

    partial void OnStrokeWidthChanged(float value)
    {
        _settings.EditorStroke = value;
        if (Selected is { } shape and not TextShape and not StepShape)
        {
            shape.StrokeWidth = value;
            IsDirty = true;
            Invalidate();
        }
    }

    partial void OnZoomChanged(double value) => Invalidate();

    [RelayCommand]
    private void ZoomIn() => ZoomStepRequested?.Invoke(1);

    [RelayCommand]
    private void ZoomOut() => ZoomStepRequested?.Invoke(-1);

    [RelayCommand]
    private void ZoomTo(string percent)
    {
        if (double.TryParse(percent, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) && p > 0)
        {
            Zoom = p / 100;
            ViewResetRequested?.Invoke();
        }
    }

    // ---------------------------------------------------------------- shape editing

    /// <summary>Arrow keys: 1 px, with Shift 10 px.</summary>
    public void Nudge(float dx, float dy)
    {
        if (Selected is not { } shape)
        {
            return;
        }

        BeginChange();
        shape.Move(dx, dy);
        Invalidate();
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is not { } shape)
        {
            return;
        }

        var copy = AnnotationDocument.FromJson(new AnnotationDocument { Shapes = [shape] }.ToJson()).Shapes[0];
        copy.Move(16, 16);
        if (copy is StepShape step)
        {
            step.Number = TakeStepNumber();
        }

        AddShape(copy);
    }

    [RelayCommand]
    private void BringToFront() => Reorder(toFront: true);

    [RelayCommand]
    private void SendToBack() => Reorder(toFront: false);

    private void Reorder(bool toFront)
    {
        if (Selected is not { } shape || !Document.Shapes.Contains(shape))
        {
            return;
        }

        BeginChange();
        Document.Shapes.Remove(shape);
        if (toFront)
        {
            Document.Shapes.Add(shape);
        }
        else
        {
            Document.Shapes.Insert(0, shape);
        }

        Invalidate();
    }

    partial void OnSelectedChanged(Shape? value)
    {
        // A selected text shows its own background in the toolbar.
        if (value is TextShape text)
        {
            _loadingPlate = true;
            TextPlate = text.EffectivePlate == TextPlate.Auto ? TextPlate.None : text.EffectivePlate;
            PlateColor = text.PlateColor;
            _loadingPlate = false;
        }

        OnPropertyChanged(nameof(ShowTextOptions));
        Invalidate();
    }

    [RelayCommand]
    private void SelectTool(string name)
    {
        if (Enum.TryParse<EditorTool>(name, out var tool))
        {
            SelectedTool = Tools.First(t => t.Tool == tool);
        }
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        _redo.Push(Document.ToJson());
        Document = AnnotationDocument.FromJson(_undo.Pop());
        AfterHistoryJump();
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        _undo.Push(Document.ToJson());
        Document = AnnotationDocument.FromJson(_redo.Pop());
        AfterHistoryJump();
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void DeleteSelected()
    {
        if (Selected is not { } shape)
        {
            return;
        }

        BeginChange();
        Document.Shapes.Remove(shape);
        Selected = null;
        Invalidate();
    }

    private bool CanDelete() => Selected is not null;

    [RelayCommand]
    private void ClearCrop() => SetCrop(null);

    [RelayCommand]
    private void ZoomFit()
    {
        Zoom = 0;
        ViewResetRequested?.Invoke();
    }

    [RelayCommand]
    private void ZoomActual()
    {
        Zoom = 1;
        ViewResetRequested?.Invoke();
    }

    [RelayCommand]
    private void Copy()
    {
        using var flat = AnnotationRenderer.Flatten(Source, Document);
        var png = AnnotationRenderer.Encode(flat, SKEncodedImageFormat.Png);
        var ok = OperatingSystem.IsWindows() && ClipboardImage.Set(AnnotationRenderer.ToPixelImage(flat), png, _settings.ExcludeFromClipboardHistory);
        Notify?.Invoke(Loc.T(ok ? "EdCopied" : "EdCopyFailed"));
    }

    /// <summary>Text recognition once per capture (stored in the .snova), then cached.</summary>
    private async Task<OcrResult?> EnsureOcrAsync()
    {
        if (Document.Ocr is not null)
        {
            return Document.Ocr;
        }

        if (Recognize is null)
        {
            return null;
        }

        IsRecognizing = true;
        try
        {
            var ocr = await Recognize();
            if (ocr is not null)
            {
                Document.Ocr = ocr;
                IsDirty = true;
            }

            return ocr;
        }
        finally
        {
            IsRecognizing = false;
        }
    }

    /// <summary>The background recognition after the capture finished while the editor was open.</summary>
    public void ApplyRecognizedText(OcrResult ocr)
    {
        Document.Ocr ??= ocr;
    }

    /// <summary>Topbar button: opens or closes the text panel (recognized text, selectable).</summary>
    [RelayCommand]
    private async Task ToggleTextPanelAsync()
    {
        if (Panel == EditorPanel.Text)
        {
            Panel = EditorPanel.None;
            return;
        }

        Panel = EditorPanel.Text;
        var ocr = await EnsureOcrAsync();
        RecognizedText = ocr is { Lines.Count: > 0 } ? ocr.FullText : "";
        if (RecognizedText.Length == 0)
        {
            Notify?.Invoke(Loc.T("EdNoText"));
        }
    }

    [RelayCommand]
    private async Task CopyAllTextAsync()
    {
        if (RecognizedText.Length == 0 || CopyTextToClipboard is null)
        {
            return;
        }

        await CopyTextToClipboard(RecognizedText);
        Notify?.Invoke(Loc.F("EdTextCopied", RecognizedText.Split('\n').Length));
    }

    [RelayCommand]
    private void ClosePanel()
    {
        if (Panel == EditorPanel.Redact)
        {
            CloseRedact();
        }

        Panel = EditorPanel.None;
    }

    /// <summary>Smart Redact: recognize text, find sensitive data, show the suggestions (nothing is applied yet).</summary>
    [RelayCommand]
    private async Task SmartRedactAsync()
    {
        // Topbar button toggles the panel.
        if (Panel == EditorPanel.Redact)
        {
            CloseRedact();
            return;
        }

        Panel = EditorPanel.Redact;
        var ocr = await EnsureOcrAsync();
        Suggestions.Clear();
        if (ocr is not null)
        {
            var words = new List<string>(_settings.RedactWords);
            if (_settings.RedactSystemNames)
            {
                words.Add(Environment.UserName);
                words.Add(Environment.MachineName);
                if (!string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                {
                    words.Add(Environment.UserDomainName);
                }
            }

            foreach (var candidate in RedactDetector.Find(ocr, words))
            {
                var suggestion = new RedactSuggestion(candidate, Loc.T("Redact" + candidate.Kind));
                suggestion.PropertyChanged += (_, _) => RaiseSelectAll();
                Suggestions.Add(suggestion);
            }
        }

        OnPropertyChanged(nameof(HasSuggestions));
        RaiseSelectAll();
        Notify?.Invoke(Suggestions.Count == 0 ? Loc.T("EdRedactNone") : Loc.F("EdRedactFound", Suggestions.Count));
    }

    [RelayCommand]
    private void ApplyRedact()
    {
        var chosen = Suggestions.Where(s => s.IsChecked).ToList();
        if (chosen.Count > 0)
        {
            BeginChange();
            foreach (var s in chosen)
            {
                var c = s.Candidate;
                Document.Shapes.Add(new RedactShape { X = c.X, Y = c.Y, Width = c.Width, Height = c.Height, Mode = _settings.RedactMode });
            }

            Invalidate();
        }

        Suggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions));
        Panel = EditorPanel.None;
    }

    [RelayCommand]
    private void CloseRedact()
    {
        Suggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions));
        Panel = EditorPanel.None;
    }

    /// <summary>Dual-use button: selects all, or – when all are selected – none.</summary>
    [RelayCommand]
    private void SelectAllSuggestions()
    {
        var all = Suggestions.All(s => s.IsChecked);
        foreach (var s in Suggestions)
        {
            s.IsChecked = !all;
        }

        RaiseSelectAll();
    }

    private void RaiseSelectAll()
    {
        OnPropertyChanged(nameof(SelectAllIcon));
        OnPropertyChanged(nameof(SelectAllTip));
    }

    public string SelectAllTip => Suggestions.Count > 0 && Suggestions.All(s => s.IsChecked) ? Loc.T("EdRedactNoneSel") : Loc.T("EdRedactAll");

    public Geometry? SelectAllIcon => Suggestions.Count > 0 && Suggestions.All(s => s.IsChecked) ? Res.Icon("IconSelectNone") : Res.Icon("IconSelectAll");

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (PickExportFile is null)
        {
            return;
        }

        var target = await PickExportFile(Path.GetFileNameWithoutExtension(FilePath), ImageExport.Format(_settings.ImageFormat).Extension);
        if (target is null)
        {
            return;
        }

        var (format, _) = ImageExport.Format(ImageExport.FromExtension(target));
        using var flat = AnnotationRenderer.Flatten(Source, Document);
        await File.WriteAllBytesAsync(target, AnnotationRenderer.Encode(flat, format, _settings.JpegQuality));
        Notify?.Invoke(Loc.F("EdExported", Path.GetFileName(target)));
    }

    [RelayCommand]
    private void Save()
    {
        SaveNow();
        Notify?.Invoke(Loc.T("EdSaved"));
    }

    [RelayCommand]
    private void SaveAndClose()
    {
        SaveNow();
        CloseRequested?.Invoke();
    }

    /// <summary>Writes the .snova (also called when the window closes with changes – SnapNova never asks "save?").</summary>
    public void SaveNow()
    {
        using var flat = AnnotationRenderer.Flatten(Source, Document);
        SnovaFile.Save(FilePath, _originalPng, Document, AnnotationRenderer.Thumbnail(flat));
        IsDirty = false;
        OnPropertyChanged(nameof(FileSizeText));
        Saved?.Invoke(FilePath);
    }

    /// <summary>The .snova was written (gallery refresh).</summary>
    public event Action<string>? Saved;

    private void AfterHistoryJump()
    {
        Selected = null;
        IsDirty = true;
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(HasCrop));
        RaiseHistory();
        Invalidate();
    }

    private void RaiseHistory()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    partial void OnIsRecognizingChanged(bool value) => Notify?.Invoke(value ? Loc.T("EdRecognizing") : "");

    private static ColorOption Swatch(uint value, string name) =>
        new(value, new SolidColorBrush(Avalonia.Media.Color.FromUInt32(value)), name);

    public void Dispose()
    {
        _settings.Save();
        Source.Dispose();
    }
}

/// <summary>A Smart Redact suggestion in the side panel (checked by default).</summary>
public sealed partial class RedactSuggestion(RedactCandidate candidate, string kindText) : ObservableObject
{
    public RedactCandidate Candidate { get; } = candidate;

    public string KindText { get; } = kindText;

    public string Text => Candidate.Text;

    [ObservableProperty]
    private bool _isChecked = true;
}
