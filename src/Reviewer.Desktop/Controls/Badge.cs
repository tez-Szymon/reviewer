using Avalonia;
using Avalonia.Controls.Primitives;
using Reviewer.Desktop.ViewModels;

namespace Reviewer.Desktop.Controls;

/// <summary>A small rounded label, coloured by its <see cref="Tone"/>; with <see cref="ShowDot"/>, a dot leads it.</summary>
public sealed class Badge : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<Badge, string?>(nameof(Text));

    public static readonly StyledProperty<Tone> ToneProperty = AvaloniaProperty.Register<Badge, Tone>(nameof(Tone));

    public static readonly StyledProperty<bool> ShowDotProperty = AvaloniaProperty.Register<Badge, bool>(nameof(ShowDot));

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Tone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public bool ShowDot
    {
        get => GetValue(ShowDotProperty);
        set => SetValue(ShowDotProperty, value);
    }
}
