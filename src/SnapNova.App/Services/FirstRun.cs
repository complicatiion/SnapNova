using SnapNova.Platform.Windows.Startup;

namespace SnapNova.App.Services;

/// <summary>
/// Applies the setup choices once per Windows user. System-wide installs cannot write every user's HKCU, so the setup
/// stores defaults in HKLM and each user gets them at their first start (the installing user right away).
/// </summary>
public static class FirstRun
{
    /// <param name="force">Installer call: apply again even if this user already had them (re-install with new choices).</param>
    public static void ApplySetupDefaults(string executable, bool force)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var settings = UiSettings.Load();
        if (settings.SetupDefaultsApplied && !force)
        {
            return;
        }

        var defaults = SetupDefaults.Read() ?? SetupDefaults.Fallback;
        settings.StartInTray = defaults.StartInTray;
        settings.MinimizeToTray = defaults.MinimizeToTray;
        settings.StartWithWindows = defaults.StartWithWindows || Autostart.IsMachineWide;
        settings.Animations = defaults.Animations;
        settings.SetupDefaultsApplied = true;
        settings.Save();

        // Portable copies never register themselves (marker file next to the exe, as in CLEANit).
        if (defaults.StartWithWindows && !AppEnvironment.IsPortable)
        {
            Autostart.Enable(executable, defaults.StartInTray);
        }
        else
        {
            Autostart.Disable();
        }
    }
}
