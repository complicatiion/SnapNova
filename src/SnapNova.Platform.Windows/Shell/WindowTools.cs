using static SnapNova.Platform.Windows.Native;

namespace SnapNova.Platform.Windows.Shell;

/// <summary>Native tweaks for SnapNova's own helper windows (overlay, recording frame, toolbar).</summary>
public static class WindowTools
{
    /// <summary>
    /// The window is invisible in screen captures and recordings (Windows 10 2004+): the recording frame and its toolbar never
    /// appear in the video. Returns false on older systems.
    /// </summary>
    public static bool ExcludeFromCapture(IntPtr hwnd) => hwnd != IntPtr.Zero && SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

    /// <summary>No taskbar button, no Alt+Tab entry, does not take the focus when clicked.</summary>
    public static void MakeToolWindow(IntPtr hwnd, bool noActivate)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var style = (ulong)GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_TOOLWINDOW;
        if (noActivate)
        {
            style |= WS_EX_NOACTIVATE;
        }

        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr((long)style));
    }
}
