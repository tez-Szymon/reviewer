using Reviewer.Git;

namespace Reviewer.Engine;

public enum DeltaKind
{
    /// <summary>The whole PR: first review, <c>--full</c>, or nothing earlier could be matched.</summary>
    Full,

    /// <summary>Commits pushed on top of the last reviewed head.</summary>
    NewCommits,

    /// <summary>A rebase or force-push: commits matched to reviewed ones by patch-id are skipped.</summary>
    Rebased,

    /// <summary>Commits couldn't be matched, but files whose change is identical to the reviewed one are skipped.</summary>
    ChangedFiles,

    /// <summary>Nothing the PR changes is new since the last review.</summary>
    Unchanged,
}

/// <summary>What the agent reviews: the diff from <see cref="FromSha"/> to the PR head, limited to <see cref="Paths"/>.</summary>
/// <param name="Paths">Paths in the head; null means every file the diff touches.</param>
/// <param name="Reason">Why this delta, for the job log and the agent's context.</param>
public sealed record ReviewDelta(DeltaKind Kind, string FromSha, IReadOnlyCollection<string>? Paths, string Reason);

/// <summary>What a review covered, kept with the PR so the next review can skip it.</summary>
/// <param name="CommitPatchIds">Patch-ids of the PR's commits, oldest first; merges and empty commits have none.</param>
/// <param name="FilePatchIds">Per-file hash of the PR's whole change, by path.</param>
public sealed record ReviewedState(string HeadSha, IReadOnlyList<string> CommitPatchIds, IReadOnlyDictionary<string, string> FilePatchIds);

/// <summary>
/// Narrows a re-review to what's new. In order: commits pushed on top of the last reviewed head; after a rebase,
/// the commits that follow the last one matched by patch-id; files whose change differs from the reviewed one;
/// otherwise the whole PR. The agent still gets the whole checkout; only the review target narrows.
/// </summary>
public static class DeltaPlanner
{
    /// <param name="mergeBase">Where the PR branches off its base.</param>
    /// <param name="previous">The last published review, or null to review the whole PR.</param>
    /// <returns>The delta, and the state to store if this review is published.</returns>
    public static async Task<(ReviewDelta Delta, ReviewedState Current)> PlanAsync(
        IGitRepository git, string mergeBase, string head, ReviewedState? previous, CancellationToken ct)
    {
        var range = $"{mergeBase}..{head}";
        var commits = await git.CommitsAsync(range, ct).ConfigureAwait(false);
        var patchIds = await git.PatchIdsAsync(range, ct).ConfigureAwait(false);
        var prPatch = await git.DiffAsync(mergeBase, head, ct).ConfigureAwait(false);
        var fileIds = FilePatchIds.Compute(prPatch);
        var current = new ReviewedState(head, [.. commits.Select(c => patchIds.GetValueOrDefault(c)).OfType<string>()], fileIds);
        var prFiles = (await git.ChangedFilesAsync(mergeBase, head, ct).ConfigureAwait(false)).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

        var full = new ReviewDelta(DeltaKind.Full, mergeBase, null, previous is null ? "First review of this PR." : "Nothing from the last review could be matched.");
        if (previous is null)
        {
            return (full, current);
        }

        var last = Short(previous.HeadSha);
        if (string.Equals(previous.HeadSha, head, StringComparison.OrdinalIgnoreCase))
        {
            return (new ReviewDelta(DeltaKind.Unchanged, head, [], $"The head is still {last}."), current);
        }

        // 1. A normal push. Limited to the PR's own files, so merging the base branch in doesn't review the base's changes.
        if (await git.HasCommitAsync(previous.HeadSha, ct).ConfigureAwait(false)
            && await git.IsAncestorAsync(previous.HeadSha, head, ct).ConfigureAwait(false))
        {
            var changed = (await git.ChangedFilesAsync(previous.HeadSha, head, ct).ConfigureAwait(false))
                .Select(f => f.Path).Where(prFiles.Contains).ToList();
            return changed.Count == 0
                ? (new ReviewDelta(DeltaKind.Unchanged, head, [], $"Commits since {last} change none of the PR's files."), current)
                : (new ReviewDelta(DeltaKind.NewCommits, previous.HeadSha, changed, $"Commits pushed since {last}."), current);
        }

        // 2. A rebase: skip the leading commits whose patch-ids were reviewed, if everything after them is new.
        // Merges and empty commits have no patch-id, so their order can't be reasoned about; fall through.
        var reviewed = previous.CommitPatchIds.ToHashSet(StringComparer.Ordinal);
        if (reviewed.Count > 0 && commits.All(patchIds.ContainsKey))
        {
            var matched = commits.TakeWhile(c => reviewed.Contains(patchIds[c])).Count();
            if (matched == commits.Count)
            {
                return (new ReviewDelta(DeltaKind.Unchanged, head, [], $"Rebased since {last}; every commit matches a reviewed one."), current);
            }

            if (matched > 0 && commits.Skip(matched).All(c => !reviewed.Contains(patchIds[c])))
            {
                var from = commits[matched - 1];
                return (new ReviewDelta(
                    DeltaKind.Rebased,
                    from,
                    [.. prFiles],
                    $"Rebased since {last}; {matched} commit(s) match reviewed ones, {commits.Count - matched} are new."), current);
            }
        }

        // 3. Per file: skip files whose whole change is the same as when it was reviewed.
        var remaining = prFiles
            .Where(p => !(fileIds.TryGetValue(p, out var id) && previous.FilePatchIds.TryGetValue(p, out var old) && id == old))
            .ToList();
        if (remaining.Count == 0)
        {
            return (new ReviewDelta(DeltaKind.Unchanged, head, [], $"History rewritten since {last}, but every file's change matches the reviewed one."), current);
        }

        // 4. Nothing matched.
        return remaining.Count == prFiles.Count
            ? (full, current)
            : (new ReviewDelta(
                DeltaKind.ChangedFiles,
                mergeBase,
                remaining,
                $"History rewritten since {last}; {prFiles.Count - remaining.Count} file(s) unchanged, {remaining.Count} changed."), current);
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
