using System.Runtime.InteropServices;

namespace SnapNova.Platform.Windows.Input;

/// <summary>Synthetic mouse input for the scrolling capture and key/button polling (Esc to stop, clicks for effects).</summary>
public static class MouseInput
{
    private const uint InputMouse = 0;
    private const uint MouseEventWheel = 0x0800;
    private const int WheelDelta = 120;

    public const int VkLeftButton = 0x01;
    public const int VkRightButton = 0x02;
    public const int VkEscape = 0x1B;
    public const int VkReturn = 0x0D;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputData
    {
        public uint Type;
        public MouseInputData Mouse;
    }

    /// <summary>Scrolls the window below the cursor by <paramref name="notches"/> wheel notches (positive = down).</summary>
    public static bool Wheel(int notches)
    {
        var input = new InputData
        {
            Type = InputMouse,
            Mouse = new MouseInputData { MouseData = unchecked((uint)(-notches * WheelDelta)), Flags = MouseEventWheel }
        };
        return SendInput(1, [input], Marshal.SizeOf<InputData>()) == 1;
    }

    public static void MoveTo(int x, int y) => SetCursorPos(x, y);

    /// <summary>True while the key or mouse button is held down.</summary>
    public static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, InputData[] inputs, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
