using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SnapNova.Platform.Windows.Shell;

/// <summary>
/// .snova → SnapNova for the current user (HKCU\Software\Classes). The setup registers the type system-wide; this
/// covers starts without setup (development build) and repairs an association whose program no longer exists. A valid
/// association of the setup is never overridden.
/// </summary>
public static class FileAssociation
{
    public const string Extension = ".snova";
    public const string ProgId = "SnapNova.Capture";

    /// <returns>True when the association was (re)written.</returns>
    public static bool EnsureRegistered(string executable, string typeName)
    {
        try
        {
            if (CommandTarget() is { } existing && File.Exists(existing))
            {
                return false;
            }

            var command = $"\"{executable}\" \"%1\"";
            using (var ext = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Extension}"))
            {
                ext.SetValue("", ProgId);
                ext.SetValue("Content Type", "application/x-snapnova");
                using var openWith = ext.CreateSubKey("OpenWithProgids");
                openWith.SetValue(ProgId, "");
            }

            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                prog.SetValue("", typeName);
                using (var icon = prog.CreateSubKey("DefaultIcon"))
                {
                    icon.SetValue("", $"\"{executable}\",0");
                }

                using var open = prog.CreateSubKey(@"shell\open\command");
                open.SetValue("", command);
            }

            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    /// <summary>Program the current association opens (HKCR = user over machine), or null.</summary>
    private static string? CommandTarget()
    {
        using var ext = Registry.ClassesRoot.OpenSubKey(Extension);
        if (ext?.GetValue("") is not string progId || progId.Length == 0)
        {
            return null;
        }

        using var command = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
        if (command?.GetValue("") is not string text)
        {
            return null;
        }

        text = text.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        return text.Split(' ')[0];
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
