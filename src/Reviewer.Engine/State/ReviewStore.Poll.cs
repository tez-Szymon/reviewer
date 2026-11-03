using Microsoft.EntityFrameworkCore;
using Reviewer.Core.Providers;

namespace Reviewer.Engine.State;

/// <summary>What the trigger rules need to know of a PR's reviews.</summary>
/// <param name="ReviewedHead">
/// The head of the PR's latest succeeded or skipped review, published or not; null when it has none, so the PR is
/// not yet followed.
/// </param>
/// <param name="HeadReviewed">The current head was reviewed already under the current config.</param>
/// <summary>An open PR in a watched repo, as <see cref="ReviewStore.ListWatchedAsync"/> lists it.</summary>
/// <param name="LastReview">Its last published review; null before the first.</param>
/// <param name="LatestJob">Its newest job in any state; null when it has none.</param>
public sealed record WatchedPullRequest(SeenPullRequestRecord Seen, PullRequestRecord? LastReview, JobSummary? LatestJob);

public sealed record ReviewHistory(string? ReviewedHead, bool HeadReviewed);

/// <remarks>The scheduler's state: each watched repo's last PR list, and its open PRs as last seen.</remarks>
public sealed partial class ReviewStore
{
    public async Task<PollStateRecord?> GetPollStateAsync(RepoRef repo, CancellationToken ct)
    {
        var (host, name) = (Host(repo), repo.ToString());
        await using var db = new ReviewerDb(_options);
        return await db.PollStates.AsNoTracking().SingleOrDefaultAsync(p => p.Host == host && p.Repo == name, ct).ConfigureAwait(false);
    }

    /// <summary>Records a poll of the repo, now, with the list's ETag and the rules it was read with.</summary>
    public async Task SavePollStateAsync(RepoRef repo, string? etag, string rulesHash, CancellationToken ct)
    {
        var (host, name) = (Host(repo), repo.ToString());
        await using var db = new ReviewerDb(_options);
        var state = await db.PollStates.SingleOrDefaultAsync(p => p.Host == host && p.Repo == name, ct).ConfigureAwait(false);
        if (state is null)
        {
            db.PollStates.Add(state = new PollStateRecord { Host = host, Repo = name, RulesHash = rulesHash });
        }

        state.ETag = etag;
        state.RulesHash = rulesHash;
        state.LastPolledAt = _time.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The repo's open PRs as last seen, by number.</summary>
    public async Task<IReadOnlyDictionary<int, SeenPullRequest>> GetSeenAsync(RepoRef repo, CancellationToken ct)
    {
        var (host, name) = (Host(repo), repo.ToString());
        await using var db = new ReviewerDb(_options);
        return await db.SeenPullRequests.AsNoTracking().Where(p => p.Host == host && p.Repo == name)
            .ToDictionaryAsync(p => p.Number, p => p.ToSeen(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the PRs as seen now, and forgets the repo's PRs that aren't open any more. PRs that are open but not in
    /// <paramref name="seen"/> keep what was recorded last.
    /// </summary>
    /// <param name="open">All the repo's open PRs; the recorded ones get their current title and author.</param>
    public async Task SaveSeenAsync(
        RepoRef repo, IReadOnlyDictionary<int, SeenPullRequest> seen, IReadOnlyCollection<PullRequestSummary> open, CancellationToken ct)
    {
        var (host, name) = (Host(repo), repo.ToString());
        var now = _time.GetUtcNow();
        var numbers = open.Select(p => p.Ref.Number).ToList();
        await using var db = new ReviewerDb(_options);
        await db.SeenPullRequests.Where(p => p.Host == host && p.Repo == name && !numbers.Contains(p.Number))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var records = await db.SeenPullRequests.Where(p => p.Host == host && p.Repo == name)
            .ToDictionaryAsync(p => p.Number, ct).ConfigureAwait(false);
        foreach (var (number, pr) in seen)
        {
            if (!records.TryGetValue(number, out var record))
            {
                db.SeenPullRequests.Add(record = new SeenPullRequestRecord { Host = host, Repo = name, Number = number, HeadSha = pr.HeadSha });
                records[number] = record;
            }

            record.HeadSha = pr.HeadSha;
            record.Matched = pr.Matched;
            record.Assigned = pr.Assigned;
            record.ReviewRequested = pr.ReviewRequested;
            record.SeenAt = now;
        }

        foreach (var pr in open)
        {
            if (records.TryGetValue(pr.Ref.Number, out var record))
            {
                (record.Title, record.Author) = (pr.Title, pr.Author);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The open PRs in watched repos as the scheduler last saw them (those its filters let through), each with its last
    /// published review and newest job. By repo, newest PR first.
    /// </summary>
    /// <param name="repo"><c>owner/name</c> to list one repo only.</param>
    public async Task<IReadOnlyList<WatchedPullRequest>> ListWatchedAsync(string? repo, CancellationToken ct)
    {
        await using var db = new ReviewerDb(_options);
        var seen = await db.SeenPullRequests.AsNoTracking().Where(p => repo == null || p.Repo == repo).ToListAsync(ct).ConfigureAwait(false);
        var repos = seen.Select(p => p.Repo).Distinct().ToList();
        var numbers = seen.Select(p => p.Number).Distinct().ToList();
        var reviews = (await db.PullRequests.AsNoTracking().Where(p => repos.Contains(p.Repo) && numbers.Contains(p.Number)).ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(p => (p.Host, p.Repo, p.Number));
        var jobs = (await db.Jobs.AsNoTracking().Where(j => repos.Contains(j.Repo) && numbers.Contains(j.Number))
                .Select(j => new JobSummary(
                    j,
                    j.Findings.Count(f => f.Disposition == FindingDisposition.Inline),
                    j.Findings.Count(f => f.Disposition == FindingDisposition.Summary),
                    j.Findings.Count(f => f.Blocking)))
                .ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(j => (j.Job.Host, j.Job.Repo, j.Job.Number))
            .ToDictionary(g => g.Key, g => g.MaxBy(j => (j.Job.CreatedAt, j.Job.Id))!);
        return [.. seen
            .OrderBy(p => p.Host, StringComparer.Ordinal).ThenBy(p => p.Repo, StringComparer.Ordinal).ThenByDescending(p => p.Number)
            .Select(p => new WatchedPullRequest(p, reviews.GetValueOrDefault((p.Host, p.Repo, p.Number)), jobs.GetValueOrDefault((p.Host, p.Repo, p.Number))))];
    }

    /// <summary>The PR's reviews as far as the trigger rules care: succeeded and skipped jobs, dry runs included.</summary>
    public async Task<ReviewHistory> GetReviewHistoryAsync(PullRequestRef pr, string headSha, string configHash, CancellationToken ct)
    {
        var (host, name) = (Host(pr.Repo), pr.Repo.ToString());
        await using var db = new ReviewerDb(_options);
        var reviewed = db.Jobs.Where(j => j.Host == host && j.Repo == name && j.Number == pr.Number
            && (j.State == JobState.Succeeded || j.State == JobState.Skipped) && j.HeadSha != null);
        var last = await reviewed.OrderByDescending(j => j.CreatedAt).Select(j => j.HeadSha).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var done = last is not null
            && await reviewed.AnyAsync(j => j.HeadSha == headSha && j.ConfigHash == configHash, ct).ConfigureAwait(false);
        return new ReviewHistory(last, done);
    }
}
