using Microsoft.EntityFrameworkCore;

namespace Reviewer.Engine.State;

/// <param name="Added">False when the PR already had a queued job and the request was merged into it.</param>
public sealed record EnqueueResult(string JobId, bool Added);

/// <remarks>
/// The queue is the <c>Jobs</c> table: queued and throttled rows are waiting, running rows are claimed by a process.
/// Every change is a conditional update on the row's state, so two processes can't both claim or settle one job.
/// </remarks>
public sealed partial class ReviewStore
{
    /// <summary>
    /// Queues a review. A PR with a job already queued for the same agent and dry-run setting gets that job back
    /// instead of a second one: the job reads the PR when it starts, so one is enough. The merged job takes the new
    /// request's <see cref="JobRecord.NotBefore"/> (a later push extends the wait, a manual request ends it) and
    /// config hash, and reviews in full if either asked for it.
    /// </summary>
    public async Task<EnqueueResult> EnqueueAsync(JobRecord job, CancellationToken ct)
    {
        await using (var db = new ReviewerDb(_options))
        {
            var queued = await db.Jobs
                .Where(j => j.State == JobState.Queued && j.Host == job.Host && j.Repo == job.Repo && j.Number == job.Number
                    && j.AgentId == job.AgentId && j.DryRun == job.DryRun)
                .OrderBy(j => j.CreatedAt)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (queued is not null)
            {
                queued.NotBefore = job.NotBefore;
                queued.ConfigHash = job.ConfigHash;
                queued.Full |= job.Full;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return new EnqueueResult(queued.Id, Added: false);
            }
        }

        job.State = JobState.Queued;
        job.CreatedAt = _time.GetUtcNow();
        return new EnqueueResult(await AddJobAsync(job, ct).ConfigureAwait(false), Added: true);
    }

    /// <summary>Queued, running and throttled jobs, oldest first.</summary>
    public async Task<IReadOnlyList<JobRecord>> ListQueueAsync(CancellationToken ct)
    {
        await using var db = new ReviewerDb(_options);
        return await db.Jobs.AsNoTracking()
            .Where(j => j.State == JobState.Queued || j.State == JobState.Running || j.State == JobState.Throttled)
            .OrderBy(j => j.CreatedAt).ThenBy(j => j.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Marks a queued or throttled job running in this process.</summary>
    /// <returns>False when the job isn't waiting any more, e.g. another process claimed or someone cancelled it.</returns>
    public async Task<bool> TryClaimJobAsync(string jobId, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        await using var db = new ReviewerDb(_options);
        return await db.Jobs
            .Where(j => j.Id == jobId && (j.State == JobState.Queued || j.State == JobState.Throttled))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(j => j.State, JobState.Running)
                    .SetProperty(j => j.StartedAt, now)
                    .SetProperty(j => j.RunnerPid, Environment.ProcessId)
                    .SetProperty(j => j.Error, (string?)null)
                    .SetProperty(j => j.RetryAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.FinishedAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.CancelRequested, false),
                ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Records what a claimed job runs with: the config may have changed since it was queued.</summary>
    public async Task SetJobConfigAsync(string jobId, string configHash, string agentId, bool dryRun, CancellationToken ct)
    {
        await using var db = new ReviewerDb(_options);
        await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(
            set => set.SetProperty(j => j.ConfigHash, configHash).SetProperty(j => j.AgentId, agentId).SetProperty(j => j.DryRun, dryRun),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels a queued or throttled job at once. A running job is only asked to stop: the worker running it cancels
    /// it and records the result.
    /// </summary>
    /// <returns>
    /// The job's state after the call: <see cref="JobState.Cancelled"/>, <see cref="JobState.Running"/> when a stop
    /// was requested, another state when there was nothing to cancel, or null for no such job.
    /// </returns>
    public async Task<JobState?> CancelJobAsync(string jobId, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        await using var db = new ReviewerDb(_options);
        var waiting = await db.Jobs
            .Where(j => j.Id == jobId && (j.State == JobState.Queued || j.State == JobState.Throttled))
            .ExecuteUpdateAsync(
                set => set.SetProperty(j => j.State, JobState.Cancelled).SetProperty(j => j.Error, "Cancelled.").SetProperty(j => j.FinishedAt, now),
                ct).ConfigureAwait(false);
        if (waiting == 0)
        {
            await db.Jobs.Where(j => j.Id == jobId && j.State == JobState.Running)
                .ExecuteUpdateAsync(set => set.SetProperty(j => j.CancelRequested, true), ct).ConfigureAwait(false);
        }

        return await db.Jobs.Where(j => j.Id == jobId).Select(j => (JobState?)j.State).SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Queues a failed, cancelled or throttled job again, to start as soon as it can.</summary>
    /// <returns>False when the job is in another state.</returns>
    public async Task<bool> RetryJobAsync(string jobId, CancellationToken ct)
    {
        await using var db = new ReviewerDb(_options);
        return await Requeue(db.Jobs.Where(j => j.Id == jobId
                && (j.State == JobState.Failed || j.State == JobState.Cancelled || j.State == JobState.Throttled)), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Queues a job again that was cancelled because the engine stopped, not because someone asked.</summary>
    public async Task<bool> RequeueInterruptedJobAsync(string jobId, CancellationToken ct)
    {
        await using var db = new ReviewerDb(_options);
        return await Requeue(db.Jobs.Where(j => j.Id == jobId && j.State == JobState.Cancelled && !j.CancelRequested), ct)
            .ConfigureAwait(false);
    }

    private static async Task<bool> Requeue(IQueryable<JobRecord> jobs, CancellationToken ct) =>
        await jobs.ExecuteUpdateAsync(
            set => set
                .SetProperty(j => j.State, JobState.Queued)
                .SetProperty(j => j.NotBefore, (DateTimeOffset?)null)
                .SetProperty(j => j.RetryAt, (DateTimeOffset?)null)
                .SetProperty(j => j.Error, (string?)null)
                .SetProperty(j => j.FinishedAt, (DateTimeOffset?)null)
                .SetProperty(j => j.CancelRequested, false),
            ct).ConfigureAwait(false) == 1;

    /// <summary>Which of the given running jobs someone asked to cancel.</summary>
    public async Task<IReadOnlyList<string>> GetCancelRequestsAsync(IReadOnlyCollection<string> jobIds, CancellationToken ct)
    {
        var ids = jobIds.ToList();
        await using var db = new ReviewerDb(_options);
        return await db.Jobs.Where(j => ids.Contains(j.Id) && j.State == JobState.Running && j.CancelRequested).Select(j => j.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Ends the job as <paramref name="state"/> if it's still running: it stopped without recording a result.</summary>
    public async Task<bool> EndIfRunningAsync(string jobId, JobState state, string error, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        await using var db = new ReviewerDb(_options);
        return await db.Jobs.Where(j => j.Id == jobId && j.State == JobState.Running).ExecuteUpdateAsync(
            set => set.SetProperty(j => j.State, state).SetProperty(j => j.Error, error).SetProperty(j => j.FinishedAt, now),
            ct).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Fails running jobs whose process is gone, so they don't hold their PR forever. They aren't queued again: one
    /// may have stopped after publishing, and running it again would post the review twice. <c>retry</c> requeues it.
    /// </summary>
    /// <param name="isRunning">Whether the job's <see cref="JobRecord.RunnerPid"/> is still running it.</param>
    /// <returns>How many jobs were failed.</returns>
    public async Task<int> FailOrphanedJobsAsync(Func<JobRecord, bool> isRunning, CancellationToken ct)
    {
        List<JobRecord> running;
        await using (var db = new ReviewerDb(_options))
        {
            running = await db.Jobs.AsNoTracking().Where(j => j.State == JobState.Running).ToListAsync(ct).ConfigureAwait(false);
        }

        var failed = 0;
        foreach (var job in running.Where(j => !isRunning(j)))
        {
            if (await EndIfRunningAsync(job.Id, JobState.Failed, "The process running this job stopped before it finished. Check the PR before retrying.", ct)
                    .ConfigureAwait(false))
            {
                failed++;
            }
        }

        return failed;
    }
}
