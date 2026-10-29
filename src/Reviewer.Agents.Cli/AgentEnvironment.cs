using System.Collections;
using System.Text.RegularExpressions;

namespace Reviewer.Agents.Cli;

/// <summary>Builds the agent's environment: the engine's own, minus anything that looks like a credential.</summary>
public static partial class AgentEnvironment
{
    /// <summary>Variables to unset (mapped to null) so the agent never sees provider tokens or other secrets.</summary>
    public static IReadOnlyDictionary<string, string?> Scrubbed(IDictionary current, IReadOnlyDictionary<string, string?> overrides)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in current)
        {
            var name = (string)entry.Key;
            if (IsSecret(name))
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

    // The agent's own auth (ANTHROPIC_*, CLAUDE_*) stays; everything else credential-shaped goes.
    public static bool IsSecret(string name) =>
        !name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase)
        && SecretName().IsMatch(name);

    [GeneratedRegex(@"TOKEN|SECRET|PASSWORD|PASSWD|CREDENTIAL|API_?KEY|ACCESS_KEY|PRIVATE_KEY|^SSH_AUTH_SOCK$|^GIT_ASKPASS$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();
}
