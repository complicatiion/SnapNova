using Microsoft.Win32;

namespace SnapNova.Platform.Windows.Input;

/// <summary>
/// Windows 11 (and Windows 10 with the option on) opens its own screen snipping with the Print key – then no other app
/// can use the key. The setting is per user: HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled.
/// </summary>
public static class PrintKey
{
    private const string KeyPath = @"Control Panel\Keyboard";
    private const string ValueName = "PrintScreenKeyForSnippingEnabled";

    /// <summary>True when Windows' snipping tool owns the Print key.</summary>
    public static bool IsUsedByWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is int value && value != 0;
        }
    }

    /// <summary>Hands the Print key back to apps (takes effect after signing out and in again, or a restart).</summary>
    public static bool ReleaseFromWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(ValueName, 0, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
