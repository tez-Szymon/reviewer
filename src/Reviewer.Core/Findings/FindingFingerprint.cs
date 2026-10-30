using System.Security.Cryptography;
using System.Text;

namespace Reviewer.Core.Findings;

/// <summary>
/// Identifies a finding across runs for dedup: category, file and the code it points at, with whitespace normalised so
/// re-indenting or moving the code doesn't change it. Computed by the engine; the model never sets it.
/// </summary>
public static class FindingFingerprint
{
    /// <param name="snippet">The finding's lines in the PR head; empty when they can't be read.</param>
    public static string Compute(Finding finding, string snippet)
    {
        var code = string.Join('\n', snippet.ReplaceLineEndings("\n").Split('\n')
            .Select(l => string.Join(' ', l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(l => l.Length > 0));
        var input = $"{finding.Category}\n{finding.File}\n{code}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)).AsSpan(0, 12));
    }
}
