using System.Collections;
using System.Text.RegularExpressions;

namespace Reviewer.Agents.Cli;

/// <summary>Builds the agent's environment: the engine's own, minus anything that looks like a credential.</summary>
public static partial class AgentEnvironment
{
    /// <summary>The variables Claude Code signs in with.</summary>
    public static IReadOnlyList<string> ClaudeAuth { get; } = ["ANTHROPIC_", "CLAUDE_"];

    /// <summary>The variables Codex signs in with.</summary>
    public static IReadOnlyList<string> CodexAuth { get; } = ["OPENAI_", "CODEX_"];

    /// <summary>Variables to unset (mapped to null) so the agent never sees provider tokens or other secrets.</summary>
    /// <param name="ownAuth">Prefixes of the agent's own sign-in variables, which it keeps.</param>
    public static IReadOnlyDictionary<string, string?> Scrubbed(
        IDictionary current, IReadOnlyDictionary<string, string?> overrides, IReadOnlyList<string> ownAuth)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in current)
        {
            var name = (string)entry.Key;
            if (IsSecret(name, ownAuth))
            {
                result[name] = null;
            }
        }

        foreach (var (name, value) in overrides)
        {
            result[name] = value;
        }

        return result;
    }

    // The agent's own auth stays; everything else credential-shaped goes, including another agent's.
    public static bool IsSecret(string name, IReadOnlyList<string> ownAuth) =>
        !ownAuth.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        && SecretName().IsMatch(name);

    [GeneratedRegex(@"TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIAL|API_?KEY|ACCESS_KEY|PRIVATE_KEY|^SSH_AUTH_SOCK$|^GIT_ASKPASS$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();
}
