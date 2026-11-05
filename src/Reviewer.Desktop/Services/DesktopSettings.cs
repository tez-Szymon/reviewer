using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reviewer.Desktop.Services;

/// <summary>Light or dark, or whichever the OS is in.</summary>
internal enum Appearance
{
    System,
    Light,
    Dark,
}

/// <summary>The app's own preferences, as <c>desktop.json</c> in the data folder holds them.</summary>
/// <param name="KeepEngineRunning">Quitting leaves an engine the app started running, so reviews go on without the window.</param>
/// <param name="Appearance">Light or dark; System follows the OS, switching when it does.</param>
internal sealed record DesktopPreferences(bool KeepEngineRunning = false, Appearance Appearance = Appearance.System);

/// <summary>
/// Reads and writes <c>desktop.json</c>. These are the app's preferences, not the engine's policy, so they stay out of
/// <c>config.yaml</c>. A missing or unreadable file means the defaults; a failed save is reported, never thrown.
/// </summary>
internal sealed class DesktopSettings(string path)
{
    public const string FileName = "desktop.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; } = path;

    public DesktopPreferences Load()
    {
        try
        {
            return JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(Path), Json) ?? new DesktopPreferences();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DesktopPreferences();
        }
    }

    /// <returns>Null when saved, otherwise why not.</returns>
    public string? Save(DesktopPreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, Json));
            File.Move(temporary, Path, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't save {FileName}: {e.Message}";
        }
    }
}
