using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Reviewer.Engine.Config;

/// <summary>
/// Where an account's token lives: <c>env:NAME</c> for an environment variable, or <c>keychain:SERVICE/ACCOUNT</c> for
/// an item in the OS credential store. Config holds only these, never the token itself.
/// </summary>
public abstract partial record TokenReference
{
    public const string EnvPrefix = "env:";
    public const string KeychainPrefix = "keychain:";

    public static bool TryParse(string value, [NotNullWhen(true)] out TokenReference? reference)
    {
        reference = EnvPattern().Match(value) is { Success: true } env ? new Env(env.Groups[1].Value)
            : KeychainPattern().Match(value) is { Success: true } item ? new Keychain(item.Groups[1].Value, item.Groups[2].Value)
            : null;
        return reference is not null;
    }

    /// <exception cref="FormatException">Not a reference; config validation rules this out for loaded accounts.</exception>
    public static TokenReference Parse(string value) =>
        TryParse(value, out var reference) ? reference : throw new FormatException("Not a token reference.");

    /// <summary>Where to look, for messages; safe to show, since it never contains the token.</summary>
    public abstract string Describe();

    public sealed record Env(string Name) : TokenReference
    {
        public override string Describe() => $"environment variable {Name}";
    }

    /// <param name="Service">The keychain item's service (macOS, libsecret); with the account, the target name on Windows.</param>
    /// <param name="Account">The keychain item's account.</param>
    public sealed record Keychain(string Service, string Account) : TokenReference
    {
        /// <summary>The Windows Credential Manager target name, as <c>cmdkey /generic:</c> takes it.</summary>
        public string Target => $"{Service}/{Account}";

        public override string Describe() => $"keychain item {Target}";
    }

    [GeneratedRegex("^env:([A-Za-z_][A-Za-z0-9_]*)$")]
    private static partial Regex EnvPattern();

    [GeneratedRegex(@"^keychain:([^/\s]+)/([^/\s]+)$")]
    private static partial Regex KeychainPattern();
}
