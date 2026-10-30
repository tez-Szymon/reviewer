namespace Reviewer.Core.Policy;

/// <summary>An inclusive range of line numbers in the PR head.</summary>
public readonly record struct LineRange(int Start, int End)
{
    public bool Overlaps(int start, int end) => start <= End && end >= Start;
}

/// <summary>What was reviewed, and as whom.</summary>
/// <param name="ReviewedHeadSha">The head SHA the agent reviewed. Merging requires the PR head to still equal it.</param>
/// <param name="TokenOwner">Login of the account whose token publishes the review.</param>
/// <param name="ChangedLines">
/// Changed head lines per file path in the delta. A file mapped to an empty list counts as changed throughout
/// (renames, mode changes).
/// </param>
public sealed record ReviewScope(
    string ReviewedHeadSha,
    string TokenOwner,
    IReadOnlyDictionary<string, IReadOnlyList<LineRange>> ChangedLines)
{
    public bool Contains(string file, int startLine, int endLine) =>
        ChangedLines.TryGetValue(file, out var ranges)
        && (ranges.Count == 0 || ranges.Any(r => r.Overlaps(startLine, endLine)));
}
