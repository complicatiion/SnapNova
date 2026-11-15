using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SnapNova.Platform.Windows.Startup;

/// <summary>
/// "Start with Windows" for the current user: HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// SnapNova runs without admin rights (asInvoker), so the Run key is the right place (no scheduled task needed as in
/// CLEANit) and the user can switch it in the app, in Settings › Apps › Startup and in Task Manager.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "SnapNova";
    public const string TrayArgument = "--tray";

    /// <summary>Argument of the machine-wide Run entry (terminal servers): the app checks the user's own setting.</summary>
    public const string MachineArgument = "--autostart";

    /// <summary>
    /// Machine-wide autostart written by the setup (HKLM Run, for every user of a terminal server / multi-session
    /// Windows). Then the per-user switch is only a setting: SnapNova ends at once when the user switched it off.
    /// </summary>
    public static bool IsMachineWide
    {
        get
        {
            using var run = Registry.LocalMachine.OpenSubKey(RunKey);
            return run?.GetValue(ValueName) is string;
        }
    }

    /// <summary>True when the Run entry exists, points to this executable and was not disabled in Task Manager.</summary>
    public static bool IsEnabled(string executable)
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(ValueName) is not string command
            || !command.Contains(executable, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsDisabledByUser();
    }

    public static void Enable(string executable, bool startInTray)
    {
        if (IsMachineWide)
        {
            // The machine-wide entry starts SnapNova already – a user entry would only start it twice.
            Disable();
            return;
        }

        var command = $"\"{executable}\"" + (startInTray ? " " + TrayArgument : "");
        using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        run.SetValue(ValueName, command, RegistryValueKind.String);

        // Switched off earlier in Task Manager / Settings › Startup: switching it on in SnapNova enables it again.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        if (approved?.GetValue(ValueName) is not null)
        {
            approved.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static void Disable()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue(ValueName, throwOnMissingValue: false);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>StartupApproved\Run: first byte 0x02/0x06 = enabled, 0x03/0x07 = disabled by the user.</summary>
    private static bool IsDisabledByUser()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return approved?.GetValue(ValueName) is byte[] { Length: > 0 } data && (data[0] & 0x01) == 0x01;
    }
}

/// <summary>Options chosen in the setup (Inno Setup writes them to HKA\Software\sksdesign\SnapNova\Setup).</summary>
public sealed record SetupDefaults(bool StartWithWindows, bool StartInTray, bool MinimizeToTray, bool Animations = true)
{
    private const string Key = @"Software\sksdesign\SnapNova\Setup";

    /// <summary>Without setup (portable, dev build): tray app defaults.</summary>
    public static SetupDefaults Fallback { get; } = new(StartWithWindows: false, StartInTray: true, MinimizeToTray: true);

    /// <summary>HKCU wins ("install for me only"), then HKLM (system-wide); null = no setup values.</summary>
    [SupportedOSPlatform("windows")]
    public static SetupDefaults? Read()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(Key);
            if (key?.GetValue("StartWithWindows") is null)
            {
                continue;
            }

            bool Flag(string name, bool fallback) => key.GetValue(name) is int value ? value != 0 : fallback;
            return new SetupDefaults(Flag("StartWithWindows", false), Flag("StartInTray", true), Flag("MinimizeToTray", true),
                Flag("Animations", true));
        }

        return null;
    }
}
