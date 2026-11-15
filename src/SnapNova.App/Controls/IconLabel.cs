using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace SnapNova.App.Controls;

/// <summary>Icon left of a text: content of all buttons and menu items.</summary>
public class IconLabel : TemplatedControl
{
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<IconLabel, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<IconLabel, string?>(nameof(Text));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<IconLabel, double>(nameof(IconSize), 16d);

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Wrap long texts (cards, notices) instead of cutting them off.</summary>
    public static readonly StyledProperty<Avalonia.Media.TextWrapping> TextWrappingProperty =
        AvaloniaProperty.Register<IconLabel, Avalonia.Media.TextWrapping>(nameof(TextWrapping), Avalonia.Media.TextWrapping.NoWrap);

    public Avalonia.Media.TextWrapping TextWrapping
    {
        get => GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }
}
