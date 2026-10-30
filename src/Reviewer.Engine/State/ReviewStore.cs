using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Reviewer.Core.Findings;
using Reviewer.Core.Providers;

namespace Reviewer.Engine.State;

/// <summary>A job for listings, with how many findings went inline and to the summary.</summary>
public sealed record JobSummary(JobRecord Job, int Inline, int SummaryOnly, int Blocking);

/// <summary>A job with its findings, in report order.</summary>
public sealed record JobDetails(JobRecord Job, IReadOnlyList<FindingRecord> Findings);

/// <summary>The PR's last published review: what it covered, and under which config.</summary>
public sealed record PreviousReview(ReviewedState State, string ConfigHash, string JobId);

/// <summary>A finding posted by an earlier review that hasn't been resolved.</summary>
/// <param name="HeadSha">The head it was reported against; its lines refer to that commit.</param>
/// <param name="Blocking">Whether it blocked when it was reported.</param>
public sealed record OpenFinding(Finding Finding, string Fingerprint, string HeadSha, bool Blocking);

/// <summary>
/// The engine's persistent state: jobs, their findings, and each PR's last published review. Each call uses its own
/// short-lived context, so the store is safe to share; SQLite's WAL mode lets a CLI read while the engine writes.
/// </summary>
public sealed partial class ReviewStore
{
    public const string FileName = "reviewer.db";

    /// <summary>The informational version of the engine assembly, recorded with each job.</summary>
    public static string AppVersion { get; } =
        typeof(ReviewStore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private readonly DbContextOptions<ReviewerDb> _options;
    private readonly TimeProvider _time;

    private ReviewStore(DbContextOptions<ReviewerDb> options, TimeProvider time)
    {
        _options = options;
        _time = time;
    }

    /// <summary>Opens the database at <paramref name="path"/>, creating it or applying pending migrations first.</summary>
    public static async Task<ReviewStore> OpenAsync(string path, TimeProvider time, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var options = ReviewerDb.Options(path);
        await using (var db = new ReviewerDb(options))
        {
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct).ConfigureAwait(false);
        }

        return new ReviewStore(options, time);
    }

    /// <summary>
    /// Records a new running job. When <paramref name="job"/>'s id is taken (two reviews of one PR in the same second),
    /// a suffix makes it unique; the returned id is the one to use for the job folder.
    /// </summary>
    public async Task<string> StartJobAsync(JobRecord job, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        job.State = JobState.Running;
        job.CreatedAt = now;
        job.StartedAt = now;
        job.RunnerPid = Environment.ProcessId;
        return await AddJobAsync(job, ct).ConfigureAwait(false);
    }

    // Inserts the job, suffixing its id when it's taken.
    private async Task<string> AddJobAsync(JobRecord job, CancellationToken ct)
    {
        var baseId = job.Id;
        for (var n = 2; ; n++)
        {
            await using var db = new ReviewerDb(_options);
            db.Jobs.Add(job);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return job.Id;
            }
            catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 } && n < 100)
            {
                job.Id = $"{baseId}-{n}"; // 1555 is SQLITE_CONSTRAINT_PRIMARYKEY: the id exists. Other constraints aren't retried.
            }
        }
    }

    /// <summary>Notes the PR head and base, what part of the PR is reviewed and, once it exists, the attempt folder.</summary>
    public async Task UpdateJobAsync(string jobId, PullRequestSnapshot snapshot, ReviewDelta delta, string? attemptFolder, CancellationToken ct)
    {
        await using var db = new ReviewerDb(_options);
        await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(
            set => set
                .SetProperty(j => j.HeadSha, snapshot.HeadSha)
                .SetProperty(j => j.BaseSha, snapshot.BaseSha)
                .SetProperty(j => j.Delta, delta.Kind)
                .SetProperty(j => j.DeltaFrom, delta.FromSha)
                .SetProperty(j => j.AttemptFolder, attemptFolder),
            ct).ConfigureAwait(false);
    }

    /// <summary>Marks the job skipped: nothing new since the last review.</summary>
    public async Task SkipJobAsync(string jobId, string reason, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        await using var db = new ReviewerDb(_options);
        await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(
            set => set.SetProperty(j => j.State, JobState.Skipped).SetProperty(j => j.Error, reason).SetProperty(j => j.FinishedAt, now),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks the job succeeded with its findings and decision. A published review also becomes the PR's last
    /// reviewed head; a dry run doesn't, since nothing reached the PR.
    /// </summary>
    /// <param name="fingerprints">Fingerprints of the report's findings.</param>
    /// <param name="state">What this review covered, kept for the next one when it's published.</param>
    /// <param name="resolved">Fingerprints of earlier findings this review found fixed; marked only when it's published.</param>
    public async Task CompleteJobAsync(
        string jobId,
        ReviewOutcome outcome,
        IReadOnlyDictionary<Finding, string> fingerprints,
        ReviewedState state,
        IReadOnlyCollection<string> resolved,
        CancellationToken ct)
    {
        var plan = outcome.Plan;
        var inline = plan.InlineFindings.ToHashSet();
        var summary = plan.SummaryFindings.ToHashSet();
        var posted = plan.AlreadyPosted.ToHashSet();
        var blocking = plan.BlockingFindingIds.ToHashSet(StringComparer.Ordinal);
        var now = _time.GetUtcNow();

        await using var db = new ReviewerDb(_options);
        var job = await db.Jobs.SingleAsync(j => j.Id == jobId, ct).ConfigureAwait(false);
        job.State = JobState.Succeeded;
        job.FinishedAt = now;
        job.HeadSha = outcome.Snapshot.HeadSha; // Open findings' lines refer to it.
        job.BaseSha = outcome.Snapshot.BaseSha;
        job.Published = outcome.Published;
        job.Decision = plan.Decision;
        job.Summary = outcome.Report.Summary;

        var ordinal = 0;
        foreach (var finding in outcome.Report.Findings)
        {
            var disposition = inline.Contains(finding) ? FindingDisposition.Inline
                : summary.Contains(finding) ? FindingDisposition.Summary
                : posted.Contains(finding) ? FindingDisposition.AlreadyPosted
                : FindingDisposition.Dropped;
            job.Findings.Add(new FindingRecord
            {
                JobId = jobId,
                Ordinal = ordinal++,
                FindingId = finding.Id,
                Severity = finding.Severity,
                Category = finding.Category,
                Confidence = finding.Confidence,
                File = finding.File,
                StartLine = finding.StartLine,
                EndLine = finding.EndLine,
                Title = finding.Title,
                Body = finding.Body,
                Suggestion = finding.Suggestion,
                Fingerprint = disposition == FindingDisposition.Dropped ? null : fingerprints.GetValueOrDefault(finding),
                Disposition = disposition,
                Blocking = disposition != FindingDisposition.Dropped && blocking.Contains(finding.Id),
            });
        }

        if (outcome.Published)
        {
            var pr = outcome.Snapshot.Ref;
            var host = Host(pr.Repo);
            var repo = pr.Repo.ToString();
            var record = await db.PullRequests
                .SingleOrDefaultAsync(p => p.Host == host && p.Repo == repo && p.Number == pr.Number, ct)
                .ConfigureAwait(false);
            if (record is null)
            {
                db.PullRequests.Add(new PullRequestRecord
                {
                    Host = host,
                    Repo = repo,
                    Number = pr.Number,
                    LastReviewedHead = outcome.Snapshot.HeadSha,
                    LastReviewedBase = outcome.Snapshot.BaseSha,
                    LastReviewedAt = now,
                    LastJobId = jobId,
                    ReviewedCommitPatchIds = [.. state.CommitPatchIds],
                    ReviewedFilePatchIds = new Dictionary<string, string>(state.FilePatchIds),
                });
            }
            else
            {
                record.LastReviewedHead = outcome.Snapshot.HeadSha;
                record.LastReviewedBase = outcome.Snapshot.BaseSha;
                record.LastReviewedAt = now;
                record.LastJobId = jobId;
                record.ReviewedCommitPatchIds = [.. state.CommitPatchIds];
                record.ReviewedFilePatchIds = new Dictionary<string, string>(state.FilePatchIds);
            }

            if (resolved.Count > 0)
            {
                var fixedIds = resolved.ToList();
                var openRows = await OpenRows(db, pr).Where(f => fixedIds.Contains(f.Fingerprint!)).ToListAsync(ct).ConfigureAwait(false);
                foreach (var row in openRows)
                {
                    row.ResolvedByJobId = jobId;
                }
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Marks the job finished without a result: failed, throttled or cancelled.</summary>
    public async Task FailJobAsync(string jobId, JobState state, string error, DateTimeOffset? retryAt, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        await using var db = new ReviewerDb(_options);
        await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(
            set => set
                .SetProperty(j => j.State, state)
                .SetProperty(j => j.Error, error)
                .SetProperty(j => j.RetryAt, retryAt)
                .SetProperty(j => j.FinishedAt, now),
            ct).ConfigureAwait(false);
    }

    /// <summary>Most recent jobs first.</summary>
    /// <param name="repo"><c>owner/name</c> to list one repo only.</param>
    public async Task<IReadOnlyList<JobSummary>> ListJobsAsync(string? repo, int limit, CancellationToken ct)
    {
        await using var db = new ReviewerDb(_options);
        var query = db.Jobs.AsNoTracking();
        if (repo is not null)
        {
            query = query.Where(j => j.Repo == repo);
        }

        return await query.OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id).Take(limit)
            .Select(j => new JobSummary(
                j,
                j.Findings.Count(f => f.Disposition == FindingDisposition.Inline),
                j.Findings.Count(f => f.Disposition == FindingDisposition.Summary),
                j.Findings.Count(f => f.Blocking)))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The job with id <paramref name="idOrPrefix"/>, or the only one whose id starts with it.</summary>
    /// <returns>Null when no job matches.</returns>
    /// <exception cref="InvalidOperationException">The prefix matches more than one job.</exception>
    public async Task<JobDetails?> GetJobAsync(string idOrPrefix, CancellationToken ct)
    {
        await using var db = new ReviewerDb(_options);
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == idOrPrefix, ct).ConfigureAwait(false);
        if (job is null)
        {
            var matches = await db.Jobs.AsNoTracking().Where(j => j.Id.StartsWith(idOrPrefix)).Take(2).ToListAsync(ct)
                .ConfigureAwait(false);
            job = matches.Count switch
            {
                0 => null,
                1 => matches[0],
                _ => throw new InvalidOperationException($"More than one job starts with '{idOrPrefix}'."),
            };
        }

        if (job is null)
        {
            return null;
        }

        var findings = await db.Findings.AsNoTracking().Where(f => f.JobId == job.Id).OrderBy(f => f.Ordinal).ToListAsync(ct)
            .ConfigureAwait(false);
        return new JobDetails(job, findings);
    }

    /// <summary>The PR's last published review with the config it ran under, or null if it has none.</summary>
    public async Task<PreviousReview?> GetPreviousReviewAsync(PullRequestRef pr, CancellationToken ct)
    {
        if (await GetPullRequestAsync(pr, ct).ConfigureAwait(false) is not { } record)
        {
            return null;
        }

        await using var db = new ReviewerDb(_options);
        var hash = await db.Jobs.Where(j => j.Id == record.LastJobId).Select(j => j.ConfigHash).SingleAsync(ct).ConfigureAwait(false);
        return new PreviousReview(record.ToReviewedState(), hash, record.LastJobId);
    }

    /// <summary>
    /// Findings posted by earlier published reviews of the PR that haven't been resolved, one per fingerprint, the
    /// earliest report of each.
    /// </summary>
    public async Task<IReadOnlyList<OpenFinding>> GetOpenFindingsAsync(PullRequestRef pr, CancellationToken ct)
    {
        await using var db = new ReviewerDb(_options);
        var rows = await OpenRows(db, pr)
            .Join(db.Jobs, f => f.JobId, j => j.Id, (f, j) => new { Finding = f, j.HeadSha, j.CreatedAt })
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Finding.Ordinal)
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. rows
            .DistinctBy(x => x.Finding.Fingerprint)
            .Select(x => new OpenFinding(x.Finding.ToFinding(), x.Finding.Fingerprint!, x.HeadSha!, x.Finding.Blocking))];
    }

    // Posted (inline or summary) by a published job of this PR, and not resolved since.
    private static IQueryable<FindingRecord> OpenRows(ReviewerDb db, PullRequestRef pr)
    {
        var host = Host(pr.Repo);
        var repo = pr.Repo.ToString();
        var jobs = db.Jobs.Where(j => j.Published && j.Host == host && j.Repo == repo && j.Number == pr.Number).Select(j => j.Id);
        return db.Findings.Where(f =>
            jobs.Contains(f.JobId)
            && f.Fingerprint != null
            && f.ResolvedByJobId == null
            && (f.Disposition == FindingDisposition.Inline || f.Disposition == FindingDisposition.Summary));
    }

    /// <summary>The PR's last published review, or null if it has none.</summary>
    public async Task<PullRequestRecord?> GetPullRequestAsync(PullRequestRef pr, CancellationToken ct)
    {
        var host = Host(pr.Repo);
        var repo = pr.Repo.ToString();
        await using var db = new ReviewerDb(_options);
        return await db.PullRequests.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Host == host && p.Repo == repo && p.Number == pr.Number, ct).ConfigureAwait(false);
    }

    public static string Host(RepoRef repo) => repo.Host.GetLeftPart(UriPartial.Authority);
}
