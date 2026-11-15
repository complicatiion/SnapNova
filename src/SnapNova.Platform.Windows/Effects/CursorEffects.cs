using System.Runtime.InteropServices;
using SnapNova.Platform.Windows.Input;

namespace SnapNova.Platform.Windows.Effects;

/// <summary>
/// Cursor highlight and click rings for video recordings: a small click-through layered window follows the mouse.
/// Unlike the recording frame it is NOT excluded from capture – it is meant to be in the video (FFmpeg draws the cursor
/// itself on top). Own Win32 window on its own thread (per-pixel alpha with UpdateLayeredWindow).
/// </summary>
public sealed class CursorEffects : IDisposable
{
    private const int Size = 112;
    private const uint WmTimer = 0x0113;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WsPopup = 0x80000000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint UlwAlpha = 0x00000002;

    private readonly bool _highlight;
    private readonly bool _clicks;
    private readonly uint _color;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private WndProc? _proc;
    private IntPtr _hwnd;
    private long _clickStart = -1;
    private bool _wasDown;
    private int _lastX = int.MinValue, _lastY;
    private long _lastFrameKey = -1;

    /// <param name="color">0xRRGGBB of the highlight and the click ring.</param>
    public CursorEffects(bool highlight, bool clicks, uint color = 0xFFC107)
    {
        _highlight = highlight;
        _clicks = clicks;
        _color = color;
        _thread = new Thread(Run) { IsBackground = true, Name = "SnapNova.CursorEffects" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        _proc = WindowProc;
        var className = "SnapNova.CursorEffects." + Environment.CurrentManagedThreadId;
        var wc = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            Instance = GetModuleHandle(null),
            ClassName = className
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(WsExLayered | WsExTransparent | WsExTopmost | WsExToolWindow | WsExNoActivate, className, "", WsPopup,
            0, 0, Size, Size, IntPtr.Zero, IntPtr.Zero, wc.Instance, IntPtr.Zero);
        SetWindowDisplayAffinity(_hwnd, 0);
        SetTimer(_hwnd, 1, 15, IntPtr.Zero);
        ShowWindow(_hwnd, 4); // SW_SHOWNOACTIVATE
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnregisterClass(className, wc.Instance);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmTimer:
                Tick();
                return IntPtr.Zero;
            case WmClose:
                KillTimer(hwnd, 1);
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private void Tick()
    {
        if (!GetCursorPos(out var p))
        {
            return;
        }

        var down = MouseInput.IsDown(MouseInput.VkLeftButton) || MouseInput.IsDown(MouseInput.VkRightButton);
        var now = Environment.TickCount64;
        if (_clicks && down && !_wasDown)
        {
            _clickStart = now;
        }

        _wasDown = down;
        var clickAge = _clickStart < 0 ? -1 : now - _clickStart;
        if (clickAge > 450)
        {
            _clickStart = -1;
            clickAge = -1;
        }

        // Redraw only when something changed (position or animation step).
        var frameKey = clickAge < 0 ? (down ? 1 : 0) : 2 + clickAge / 15;
        if (p.X == _lastX && p.Y == _lastY && frameKey == _lastFrameKey && clickAge < 0)
        {
            return;
        }

        _lastX = p.X;
        _lastY = p.Y;
        _lastFrameKey = frameKey;
        Draw(p.X - Size / 2, p.Y - Size / 2, down, clickAge);
    }

    private void Draw(int left, int top, bool down, long clickAge)
    {
        var pixels = new byte[Size * Size * 4];
        var r = (byte)(_color >> 16);
        var g = (byte)(_color >> 8);
        var b = (byte)_color;
        const double center = Size / 2.0;

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var d = Math.Sqrt((x + 0.5 - center) * (x + 0.5 - center) + (y + 0.5 - center) * (y + 0.5 - center));
                double alpha = 0;

                // Highlight: soft disc behind the cursor (stronger while a button is held).
                if (_highlight)
                {
                    var radius = down ? 20.0 : 24.0;
                    alpha = Math.Max(alpha, Math.Clamp(radius - d + 1, 0, 1) * (down ? 0.55 : 0.38));
                }

                // Click: ring that grows and fades.
                if (clickAge >= 0)
                {
                    var t = clickAge / 450.0;
                    var ringRadius = 12 + t * 40;
                    var ring = Math.Clamp(3.0 - Math.Abs(d - ringRadius), 0, 1);
                    alpha = Math.Max(alpha, ring * (1 - t) * 0.95);
                }

                if (alpha <= 0)
                {
                    continue;
                }

                // Premultiplied BGRA.
                var i = (y * Size + x) * 4;
                var a = (byte)(alpha * 255);
                pixels[i] = (byte)(b * alpha);
                pixels[i + 1] = (byte)(g * alpha);
                pixels[i + 2] = (byte)(r * alpha);
                pixels[i + 3] = a;
            }
        }

        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = Size, biHeight = -Size, biPlanes = 1, biBitCount = 32
        };
        var bitmap = Native.CreateDIBSection(screen, ref header, 0, out var bits, IntPtr.Zero, 0);
        var old = Native.SelectObject(memory, bitmap);
        try
        {
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var position = new Native.POINT { X = left, Y = top };
            var size = new SizeStruct { Cx = Size, Cy = Size };
            var source = new Native.POINT();
            var blend = new BlendFunction { Op = 0, Flags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(_hwnd, screen, ref position, ref size, memory, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            Native.SelectObject(memory, old);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }

        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeStruct
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte Op;
        public byte Flags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassEx(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "UnregisterClassW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out Native.MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Native.MSG msg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(ref Native.MSG msg);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint elapse, IntPtr timerProc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Native.POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screenDc, ref Native.POINT position, ref SizeStruct size, IntPtr sourceDc,
        ref Native.POINT sourcePosition, uint colorKey, ref BlendFunction blend, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandle(string? module);
}
