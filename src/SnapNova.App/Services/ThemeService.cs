using Avalonia;
using Avalonia.Styling;

namespace SnapNova.App.Services;

/// <summary>Dark (default), light or "system" (follows the Windows app mode) for all windows, including the native title bars.</summary>
public static class ThemeService
{
    public static void Apply(string? theme)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = theme?.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
        WindowChrome.RefreshAll();
    }
}
