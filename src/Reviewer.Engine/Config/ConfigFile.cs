using System.Security.Cryptography;
using System.Text;

namespace Reviewer.Engine.Config;

/// <summary><c>config.yaml</c> as text, with a hash of it that a save must name, so it never overwrites an edit it didn't see.</summary>
/// <param name="Yaml">Empty when there's no file.</param>
public sealed record ConfigText(string Yaml, string Hash);

/// <summary>The file changed since the client read it: by hand, or by another save.</summary>
public sealed class ConfigConflictException(string current) : Exception("config.yaml changed since it was read; read it again and reapply the change.")
{
    public string CurrentHash { get; } = current;
}

/// <summary>
/// Reads and saves <c>config.yaml</c> for the app. A save is validated first, refused unless the file still has the hash
/// the client read, and written to a temporary file that replaces the old one, so a reader never sees half a file.
/// </summary>
public static class ConfigFile
{
    // Saves from this process run one at a time; a hand edit between the check and the write is the one race left.
    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    public static async Task<ConfigText> ReadAsync(string path, CancellationToken ct)
    {
        string yaml;
        try
        {
            yaml = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            yaml = "";
        }

        return new ConfigText(yaml, Hash(yaml));
    }

    /// <summary>Saves <paramref name="yaml"/> in place of the file that had <paramref name="baseHash"/>.</summary>
    /// <exception cref="ConfigException">The new config is invalid; nothing is written.</exception>
    /// <exception cref="ConfigConflictException">The file no longer has <paramref name="baseHash"/>.</exception>
    public static Task<ConfigText> SaveAsync(string path, string yaml, string baseHash, CancellationToken ct) =>
        UpdateAsync(path, baseHash, _ => yaml, ct);

    /// <summary>Applies <paramref name="edits"/> to the file that had <paramref name="baseHash"/> and saves the result.</summary>
    /// <exception cref="ConfigException">An edit doesn't fit the file, or the result is invalid; nothing is written.</exception>
    /// <exception cref="ConfigConflictException">The file no longer has <paramref name="baseHash"/>.</exception>
    public static Task<ConfigText> EditAsync(string path, IReadOnlyList<ConfigEdit> edits, string baseHash, CancellationToken ct) =>
        UpdateAsync(path, baseHash, yaml => ConfigEditor.Apply(yaml, edits), ct);

    private static async Task<ConfigText> UpdateAsync(string path, string baseHash, Func<string, string> change, CancellationToken ct)
    {
        await SaveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(path, ct).ConfigureAwait(false);
            if (!string.Equals(current.Hash, baseHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigConflictException(current.Hash);
            }

            var yaml = change(current.Yaml);
            ConfigLoader.Parse(yaml, path);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = $"{path}.{Environment.ProcessId}.tmp";
            await File.WriteAllTextAsync(temp, yaml, new UTF8Encoding(false), ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            return new ConfigText(yaml, Hash(yaml));
        }
        finally
        {
            SaveLock.Release();
        }
    }

    private static string Hash(string yaml) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(yaml)))[..16];
}
