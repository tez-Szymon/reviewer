using Microsoft.Extensions.FileSystemGlobbing;
using Reviewer.Core.Providers;
using Reviewer.Engine.Config;

namespace Reviewer.Engine;

/// <summary>A PR as the scheduler saw it on an earlier poll.</summary>
/// <param name="Matched">An initial trigger matched.</param>
/// <param name="Assigned">The token owner was among the assignees.</param>
/// <param name="ReviewRequested">A review was requested from the token owner.</param>
public sealed record SeenPullRequest(string HeadSha, bool Matched, bool Assigned, bool ReviewRequested);

/// <summary>Why the scheduler queues a review.</summary>
public enum TriggerReason
{
    /// <summary>An initial trigger matches a PR that hasn't been reviewed.</summary>
    Initial,
    NewCommits,
    Assigned,
    ReviewRequested,
}

/// <param name="NotBefore">Null to start as soon as the queue allows; set to wait out a burst of pushes.</param>
public sealed record TriggerDecision(IReadOnlyList<TriggerReason> Reasons, DateTimeOffset? NotBefore);

/// <summary>
/// Decides from a PR's state now and when last seen whether it's due a review. Pure: the scheduler supplies what the
/// provider and the store know.
/// </summary>
/// <remarks>
/// <para>
/// Rules act on changes, not on state, so a PR is queued once per event: a push, an assignment, a review request, or an
/// initial trigger starting to match. A PR nobody changed is left alone, even when its review failed or was cancelled.
/// </para>
/// <para>
/// A PR is <em>followed</em> once it has a succeeded or skipped review. Until then the initial triggers decide; after
/// that the re-review triggers do, whether or not an initial trigger still matches. On GitHub a review request ends
/// when the review is posted, so <c>review-requested</c> alone could never re-review a PR on new commits.
/// </para>
/// </remarks>
public static class TriggerRules
{
    /// <summary>Whether the scheduler acts on the PR at all: not a skipped draft, and into a watched branch.</summary>
    public static bool PassesFilters(PullRequestSummary pr, TriggerConfig triggers) =>
        !(triggers.SkipDrafts && pr.IsDraft)
        && (triggers.TargetBranches.Count == 0 || triggers.TargetBranches.Contains(pr.BaseBranch, StringComparer.Ordinal));

    /// <summary>The PR's state for the rules, as it will be recorded for the next poll.</summary>
    /// <param name="me">The token owner's login.</param>
    public static SeenPullRequest See(PullRequestSummary pr, TriggerConfig triggers, string me)
    {
        var assigned = pr.Assignees.Contains(me, StringComparer.OrdinalIgnoreCase);
        var requested = pr.RequestedReviewers.Contains(me, StringComparer.OrdinalIgnoreCase);
        var matched = triggers.Initial.Any(trigger => trigger switch
        {
            InitialTrigger.All => true,
            InitialTrigger.AssignedToMe => assigned,
            InitialTrigger.ReviewRequested => requested,
            InitialTrigger.HasLabel => pr.Labels.Any(l => triggers.Labels.Contains(l, StringComparer.OrdinalIgnoreCase)),
            InitialTrigger.AuthoredBy => triggers.Authors.Contains(pr.Author, StringComparer.OrdinalIgnoreCase),
            _ => false,
        });
        return new SeenPullRequest(pr.HeadSha, matched, assigned, requested);
    }

    /// <summary>Whether the PR is due a review, and when it may start. Null when it isn't.</summary>
    /// <param name="now">The PR as it is, from <see cref="See"/>.</param>
    /// <param name="before">The PR on the last poll that passed the filters; null when it wasn't seen then.</param>
    /// <param name="reviewedHead">The head of the PR's latest review; null when it isn't followed yet.</param>
    public static TriggerDecision? Decide(SeenPullRequest now, SeenPullRequest? before, string? reviewedHead, TriggerConfig triggers, DateTimeOffset at)
    {
        if (reviewedHead is null)
        {
            // A push re-checks a PR the size or path filters turned away, and retries one whose review failed.
            return now.Matched && (before is null || !before.Matched || before.HeadSha != now.HeadSha)
                ? new TriggerDecision([TriggerReason.Initial], null)
                : null;
        }

        var rules = triggers.ReReview;
        var reasons = new List<TriggerReason>();
        // A PR not seen before was pushed to while the engine wasn't watching if its head isn't the reviewed one.
        if (rules.OnNewCommits && now.HeadSha != (before?.HeadSha ?? reviewedHead))
        {
            reasons.Add(TriggerReason.NewCommits);
        }

        // Without an earlier poll there's no telling whether an assignment or request is new.
        if (rules.OnReassignment && before is not null && now.Assigned && !before.Assigned)
        {
            reasons.Add(TriggerReason.Assigned);
        }

        if (rules.OnReviewRequested && before is not null && now.ReviewRequested && !before.ReviewRequested)
        {
            reasons.Add(TriggerReason.ReviewRequested);
        }

        return reasons switch
        {
            [] => null,
            [TriggerReason.NewCommits] => new TriggerDecision(reasons, at + TimeSpan.FromMinutes(rules.DebounceMinutes)),
            // Someone asked for a review: start it now, whatever the pushes.
            _ => new TriggerDecision(reasons, null),
        };
    }

    /// <summary>Why the PR's changes rule out a review: too large, or touching no watched path. Null when they don't.</summary>
    public static string? RejectChanges(IReadOnlyList<ChangedFile> files, TriggerConfig triggers)
    {
        var lines = files.Sum(f => (long)f.Additions + f.Deletions);
        if (lines > triggers.MaxDiffLines)
        {
            return $"it changes {lines} lines, over maxDiffLines ({triggers.MaxDiffLines})";
        }

        if (triggers.Paths.Count > 0)
        {
            var matcher = new Matcher(StringComparison.Ordinal);
            matcher.AddIncludePatterns(triggers.Paths);
            if (!matcher.Match(files.Select(f => f.Path)).HasMatches)
            {
                return "it touches none of the trigger paths";
            }
        }

        return null;
    }
}
