using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SnapNova.App.Localization;

/// <summary>Language with flat flag icon. "auto" (first entry, default) follows the Windows display language.</summary>
public sealed record LanguageOption(string Code, string Name, Bitmap? Flag)
{
    public const string AutoCode = "auto";

    private static readonly Lazy<IReadOnlyList<LanguageOption>> Catalog = new(() =>
        new[] { new LanguageOption(AutoCode, "Auto (Windows)", null) }
            .Concat(Loc.Languages.Select(l => new LanguageOption(l.Code, l.Name, LoadFlag(l.Code))))
            .ToList());

    public static IReadOnlyList<LanguageOption> All => Catalog.Value;

    public bool IsAuto => Code == AutoCode;

    /// <summary>The option for the stored setting (null = auto).</summary>
    public static LanguageOption ForSetting(string? code) =>
        code is null ? All[0] : All.FirstOrDefault(l => l.Code == code) ?? All[0];

    private static Bitmap? LoadFlag(string code)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://SnapNova/Assets/flag-{code}.png"));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    public override string ToString() => Name;
}
