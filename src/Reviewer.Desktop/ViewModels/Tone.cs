namespace Reviewer.Desktop.ViewModels;

/// <summary>What a state says at a glance, as a badge's colour shows it.</summary>
public enum Tone
{
    Neutral,

    /// <summary>In progress, or for information.</summary>
    Accent,
    Success,
    Warning,
    Danger,
}
