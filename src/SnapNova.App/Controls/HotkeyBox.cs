using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using HotkeyModel = SnapNova.Core.Capture.Hotkey;

namespace SnapNova.App.Controls;

/// <summary>
/// Records a key combination: click, press e.g. Ctrl+Shift+S. Backspace/Delete clears, Esc keeps the old value.
/// A combination needs at least one modifier (except F-keys and Print Screen).
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly StyledProperty<string?> HotkeyProperty =
        AvaloniaProperty.Register<HotkeyBox, string?>(nameof(Hotkey), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    protected override Type StyleKeyOverride => typeof(TextBox);

    public HotkeyBox()
    {
        Classes.Add("hotkey");
        IsReadOnly = true;
        IsUndoEnabled = false;
    }

    public string? Hotkey
    {
        get => GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HotkeyProperty)
        {
            Text = string.IsNullOrEmpty(Hotkey) ? "–" : Hotkey;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Escape:
                Text = string.IsNullOrEmpty(Hotkey) ? "–" : Hotkey;
                TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
                return;
            case Key.Back or Key.Delete when e.KeyModifiers == KeyModifiers.None:
                Hotkey = "";
                return;
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin:
                return;
        }

        var vk = VirtualKey(e.Key);
        if (vk == 0)
        {
            return;
        }

        var mods = e.KeyModifiers;
        var standalone = vk is >= 0x70 and <= 0x87 or 0x2C or 0x13;
        if (!standalone && (mods & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0)
        {
            return;
        }

        Hotkey = new HotkeyModel(mods.HasFlag(KeyModifiers.Control), mods.HasFlag(KeyModifiers.Alt), mods.HasFlag(KeyModifiers.Shift),
            mods.HasFlag(KeyModifiers.Meta), vk).ToString();
    }

    /// <summary>Avalonia key → Windows virtual-key code (letters, digits, F-keys, numpad, a few specials).</summary>
    private static uint VirtualKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (uint)('A' + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (uint)('0' + (key - Key.D0)),
        >= Key.NumPad0 and <= Key.NumPad9 => (uint)(0x60 + (key - Key.NumPad0)),
        >= Key.F1 and <= Key.F24 => (uint)(0x70 + (key - Key.F1)),
        Key.PrintScreen => 0x2C,
        Key.Pause => 0x13,
        Key.Insert => 0x2D,
        Key.Home => 0x24,
        Key.End => 0x23,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.Space => 0x20,
        _ => 0
    };
}
