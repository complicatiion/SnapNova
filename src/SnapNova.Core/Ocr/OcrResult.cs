namespace SnapNova.Core.Ocr;

/// <summary>A recognized word with its bounds in pixels of the original image.</summary>
public sealed record OcrWord(string Text, float X, float Y, float Width, float Height);

public sealed record OcrLine(IReadOnlyList<OcrWord> Words)
{
    public string Text => string.Join(' ', Words.Select(w => w.Text));
}

/// <summary>Result of the text recognition, stored in document.json of the .snova.</summary>
public sealed record OcrResult(IReadOnlyList<OcrLine> Lines, string? Language)
{
    public string FullText => string.Join('\n', Lines.Select(l => l.Text));
}
