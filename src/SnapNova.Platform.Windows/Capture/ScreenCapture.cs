using System.Diagnostics;
using System.Runtime.InteropServices;
using SnapNova.Core.Capture;
using static SnapNova.Platform.Windows.Native;

namespace SnapNova.Platform.Windows.Capture;

/// <summary>A monitor in virtual-desktop pixels (the process is per-monitor DPI aware, so these are physical pixels).</summary>
public sealed record MonitorInfo(string DeviceName, ScreenRect Bounds, ScreenRect WorkArea, bool IsPrimary);

/// <summary>
/// Freezes the screen: all monitors (GDI BitBlt with CAPTUREBLT, layered windows and menus included) and the window list
/// for snapping, taken in one go before any SnapNova overlay is shown.
/// </summary>
public static class ScreenCapture
{
    public static IReadOnlyList<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
            if (GetMonitorInfo(monitor, ref info))
            {
                list.Add(new MonitorInfo(info.szDevice, ToRect(info.rcMonitor), ToRect(info.rcWork), (info.dwFlags & 1) != 0));
            }

            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static FrozenScreen Freeze(bool includeCursor)
    {
        // Window list first: it must not contain anything that appears because of the capture itself.
        var windows = WindowList.Snapshot();
        var shots = Monitors()
            .Select(m => new MonitorShot(m.DeviceName, m.Bounds, m.IsPrimary, Grab(m.Bounds, includeCursor)))
            .ToList();
        return new FrozenScreen(shots, windows, DateTimeOffset.Now);
    }

    public static (int X, int Y) CursorPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    /// <summary>Copies a part of the virtual desktop into a BGRA image (alpha set to opaque).</summary>
    public static PixelImage Grab(ScreenRect area, bool includeCursor)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = area.Width,
            biHeight = -area.Height, // negative = top-down rows
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0 // BI_RGB
        };
        var bitmap = CreateDIBSection(screenDc, ref header, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
        {
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            throw new InvalidOperationException($"CreateDIBSection failed for {area}.");
        }

        var old = SelectObject(memoryDc, bitmap);
        try
        {
            BitBlt(memoryDc, 0, 0, area.Width, area.Height, screenDc, area.X, area.Y, SRCCOPY | CAPTUREBLT);
            if (includeCursor)
            {
                DrawCursor(memoryDc, area);
            }

            var stride = area.Width * 4;
            var pixels = new byte[stride * area.Height];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            // GDI leaves alpha undefined (mostly 0): screen content is opaque.
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }

            return new PixelImage(area.Width, area.Height, pixels, stride);
        }
        finally
        {
            SelectObject(memoryDc, old);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void DrawCursor(IntPtr dc, ScreenRect area)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero)
        {
            return;
        }

        var hotX = 0;
        var hotY = 0;
        if (GetIconInfo(info.hCursor, out var icon))
        {
            hotX = icon.xHotspot;
            hotY = icon.yHotspot;
            if (icon.hbmMask != IntPtr.Zero)
            {
                DeleteObject(icon.hbmMask);
            }

            if (icon.hbmColor != IntPtr.Zero)
            {
                DeleteObject(icon.hbmColor);
            }
        }

        DrawIconEx(dc, info.ptScreenPos.X - hotX - area.X, info.ptScreenPos.Y - hotY - area.Y, info.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
    }

    internal static ScreenRect ToRect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}

/// <summary>Visible top-level windows in z-order (topmost first) with their visible frame (without the invisible resize border).</summary>
public static class WindowList
{
    private static readonly HashSet<string> SkippedClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"
    };

    /// <summary>Process id of a window (0 if unknown).</summary>
    public static int ProcessIdOf(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    public static IReadOnlyList<WindowInfo> Snapshot()
    {
        var own = Environment.ProcessId;
        var names = new Dictionary<uint, string>();
        var list = new List<WindowInfo>();
        var title = new char[512];
        var className = new char[256];

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
            {
                return true;
            }

            // Cloaked: on another virtual desktop, suspended UWP app, hidden by the shell.
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            var classLength = GetClassName(hwnd, className, className.Length);
            if (classLength > 0 && SkippedClasses.Contains(new string(className, 0, classLength)))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == own)
            {
                return true;
            }

            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) != 0
                && !GetWindowRect(hwnd, out frame))
            {
                return true;
            }

            var bounds = ScreenCapture.ToRect(frame);
            if (bounds.Width < 8 || bounds.Height < 8)
            {
                return true;
            }

            var length = GetWindowText(hwnd, title, title.Length);
            list.Add(new WindowInfo(hwnd, bounds, length > 0 ? new string(title, 0, length) : "", ProcessName(pid, names)));
            return true;
        }, IntPtr.Zero);

        return list;
    }

    private static string ProcessName(uint pid, Dictionary<uint, string> cache)
    {
        if (cache.TryGetValue(pid, out var name))
        {
            return name;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            name = "";
        }

        cache[pid] = name;
        return name;
    }
}
