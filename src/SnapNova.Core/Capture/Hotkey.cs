using System.Text;

namespace SnapNova.Core.Capture;

/// <summary>Global keyboard shortcut: modifiers + Windows virtual-key code. Stored as text ("Ctrl+Shift+S").</summary>
public readonly record struct Hotkey(bool Ctrl, bool Alt, bool Shift, bool Win, uint VirtualKey)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    public bool IsEmpty => VirtualKey == 0;

    /// <summary>RegisterHotKey modifiers (with MOD_NOREPEAT: holding the keys does not fire again).</summary>
    public uint Modifiers =>
        (Ctrl ? ModControl : 0) | (Alt ? ModAlt : 0) | (Shift ? ModShift : 0) | (Win ? ModWin : 0) | ModNoRepeat;

    public override string ToString()
    {
        if (IsEmpty)
        {
            return "";
        }

        var text = new StringBuilder();
        if (Ctrl)
        {
            text.Append("Ctrl+");
        }

        if (Alt)
        {
            text.Append("Alt+");
        }

        if (Shift)
        {
            text.Append("Shift+");
        }

        if (Win)
        {
            text.Append("Win+");
        }

        return text.Append(KeyName(VirtualKey)).ToString();
    }

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        bool ctrl = false, alt = false, shift = false, win = false;
        uint vk = 0;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    ctrl = true;
                    break;
                case "alt":
                    alt = true;
                    break;
                case "shift":
                    shift = true;
                    break;
                case "win":
                    win = true;
                    break;
                default:
                    vk = KeyCode(part);
                    break;
            }
        }

        return vk == 0 ? default : new Hotkey(ctrl, alt, shift, win, vk);
    }

    public static string KeyName(uint vk) => vk switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        >= 0x60 and <= 0x69 => "Num" + (vk - 0x60),
        0x2C => "PrintScreen",
        0x13 => "Pause",
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x20 => "Space",
        _ => "0x" + vk.ToString("X2")
    };

    public static uint KeyCode(string name)
    {
        var n = name.Trim();
        if (n.Length == 1 && char.IsAsciiLetterOrDigit(n[0]))
        {
            return char.ToUpperInvariant(n[0]);
        }

        if (n.Length is 2 or 3 && (n[0] == 'F' || n[0] == 'f') && int.TryParse(n[1..], out var f) && f is >= 1 and <= 24)
        {
            return (uint)(0x6F + f);
        }

        if (n.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && int.TryParse(n[3..], out var num) && num is >= 0 and <= 9)
        {
            return (uint)(0x60 + num);
        }

        return n.ToLowerInvariant() switch
        {
            "printscreen" => 0x2C,
            "pause" => 0x13,
            "insert" => 0x2D,
            "delete" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "space" => 0x20,
            _ when n.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                   && uint.TryParse(n[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex) => hex,
            _ => 0
        };
    }
}
