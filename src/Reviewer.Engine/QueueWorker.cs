using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Reviewer.Engine.State;

namespace Reviewer.Engine;

/// <summary>Runs one claimed job; <see cref="QueueWorker"/> decides which and when.</summary>
public interface IJobLauncher
{
    /// <summary>How many jobs the agent may run at once.</summary>
    int MaxConcurrency(string agentId);

    /// <summary>
    /// Reviews a job the worker has claimed and records the result, as <see cref="ReviewRunner.RunQueuedAsync"/>
    /// does. A job still running when this returns or throws is ended by the worker.
    /// </summary>
    Task RunAsync(JobRecord job, CancellationToken ct);
}

/// <summary>
/// Takes jobs off the queue in the store and runs them, until stopped. A job starts when it's due (its
/// <see cref="JobRecord.NotBefore"/>, or a throttled job's retry time) and its agent has a free slot, and never while
/// another job for the same PR is running, in this process or another.
/// </summary>
/// <remarks>
/// <para>
/// A usage limit is the subscription's, not the job's, so a throttled job pauses its agent: nothing else starts on it
/// until the latest retry time passes. A throttle without a retry time waits <see cref="ThrottleBackoff"/>.
/// </para>
/// <para>
/// The store is the queue, so jobs added by another process (the CLI) are picked up within
/// <see cref="PollInterval"/>; <see cref="Wake"/> starts a pass at once. Stopping cancels the running jobs and queues
/// them again; jobs a dead process left running are failed on start, since they may have published already.
/// </para>
/// </remarks>
public sealed class QueueWorker(ReviewStore store, IJobLauncher launcher, TimeProvider time, Action<string>? log = null) : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan ThrottleBackoff = TimeSpan.FromMinutes(15);

    // Touched by the loop only; finished jobs are pruned at the start of each pass.
    private readonly Dictionary<string, RunningJob> _running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile bool _stopping;

    /// <summary>The jobs this worker has started, for tests to wait on.</summary>
    internal IReadOnlyCollection<Task> Running => [.. _running.Values.Select(r => r.Task)];

    /// <summary>Looks at the queue now instead of at the next poll, e.g. after adding a job.</summary>
    public void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>Runs jobs until <paramref name="ct"/> is cancelled, then stops the running ones and queues them again.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var orphans = await store.FailOrphanedJobsAsync(IsRunning, ct).ConfigureAwait(false);
            if (orphans > 0)
            {
                log?.Invoke($"Marked {orphans} job(s) failed: the process running them stopped. Check their PRs, then 'reviewer queue retry'.");
            }

            while (true)
            {
                TimeSpan wait;
                try
                {
                    wait = await TickAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is DbException or DbUpdateException)
                {
                    log?.Invoke($"Couldn't read the queue: {(e.InnerException ?? e).Message}");
                    wait = PollInterval;
                }

                await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            _stopping = true;
            foreach (var job in _running.Values)
            {
                await job.Cancel.CancelAsync().ConfigureAwait(false);
            }

            await Task.WhenAll(_running.Values.Select(r => r.Task)).ConfigureAwait(false);
        }
    }

    /// <summary>One pass: stops jobs someone cancelled and starts the ones that are due.</summary>
    /// <returns>How long to wait before the next pass, unless something wakes the worker sooner.</returns>
    internal async Task<TimeSpan> TickAsync(CancellationToken ct)
    {
        foreach (var (id, done) in _running.Where(r => r.Value.Task.IsCompleted).ToList())
        {
            _running.Remove(id);
            done.Cancel.Dispose();
        }

        if (_running.Count > 0)
        {
            foreach (var id in await store.GetCancelRequestsAsync(_running.Keys, ct).ConfigureAwait(false))
            {
                await _running[id].Cancel.CancelAsync().ConfigureAwait(false);
            }
        }

        var now = time.GetUtcNow();
        var (due, next) = Pick(await store.ListQueueAsync(ct).ConfigureAwait(false), now, launcher.MaxConcurrency);
        foreach (var job in due)
        {
            if (await store.TryClaimJobAsync(job.Id, ct).ConfigureAwait(false))
            {
                var cancel = new CancellationTokenSource();
                _running[job.Id] = new RunningJob(RunJobAsync(job, cancel.Token), cancel);
            }
        }

        return next is { } at && at - now < PollInterval ? at - now : PollInterval;
    }

    /// <summary>The waiting jobs to start now, oldest due first, and when the next one that has to wait becomes due.</summary>
    /// <param name="active">Queued, running and throttled jobs, as <see cref="ReviewStore.ListQueueAsync"/> returns them.</param>
    /// <returns>
    /// <c>Next</c> covers jobs waiting for a time only; one waiting for a slot or for its PR starts when a job ends.
    /// </returns>
    internal static (IReadOnlyList<JobRecord> Due, DateTimeOffset? Next) Pick(
        IReadOnlyList<JobRecord> active, DateTimeOffset now, Func<string, int> maxConcurrency)
    {
        var running = active.Where(j => j.State == JobState.Running).ToList();
        var busy = running.Select(PrKey).ToHashSet();
        var slotsUsed = running.GroupBy(j => j.AgentId).ToDictionary(g => g.Key, g => g.Count());
        var pausedUntil = active.Where(j => j.State == JobState.Throttled).GroupBy(j => j.AgentId).ToDictionary(g => g.Key, g => g.Max(DueAt));

        // A paused agent's jobs are due when the pause ends; among those, the oldest goes first.
        var waiting = active.Where(j => j.State != JobState.Running)
            .Select(j => (Job: j, At: pausedUntil.TryGetValue(j.AgentId, out var paused) && paused > DueAt(j) ? paused : DueAt(j)))
            .OrderBy(w => w.At).ThenBy(w => w.Job.CreatedAt);
        var due = new List<JobRecord>();
        DateTimeOffset? next = null;
        foreach (var (job, at) in waiting)
        {
            if (at > now)
            {
                next = next is { } n && n < at ? n : at;
                continue;
            }

            var used = slotsUsed.GetValueOrDefault(job.AgentId);
            if (busy.Contains(PrKey(job)) || used >= Math.Max(1, maxConcurrency(job.AgentId)))
            {
                continue;
            }

            due.Add(job);
            busy.Add(PrKey(job));
            slotsUsed[job.AgentId] = used + 1;
        }

        return (due, next);
    }

    // When a waiting job may start, ignoring slots and its agent's pause.
    private static DateTimeOffset DueAt(JobRecord job) => job.State == JobState.Throttled
        ? job.RetryAt ?? (job.FinishedAt ?? job.CreatedAt) + ThrottleBackoff
        : job.NotBefore ?? job.CreatedAt;

    // Hosts and repo names are case-insensitive, as in the store.
    private static (string, string, int) PrKey(JobRecord job) => (job.Host.ToLowerInvariant(), job.Repo.ToLowerInvariant(), job.Number);

    private async Task RunJobAsync(JobRecord job, CancellationToken ct)
    {
        await Task.Yield(); // Don't run the job's synchronous start inside the pass that claimed it.
        var name = $"{job.Repo}#{job.Number} ({job.Id})";
        log?.Invoke($"Started {name}.");
        string? error = null;
        try
        {
            await launcher.RunAsync(job, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            error = e.Message;
            log?.Invoke($"{name}: {e.Message}");
        }

        try
        {
            // The launcher failed before the runner recorded anything, e.g. on a config or token problem.
            if (ct.IsCancellationRequested)
            {
                await store.EndIfRunningAsync(job.Id, JobState.Cancelled, "Cancelled.", CancellationToken.None).ConfigureAwait(false);
                if (_stopping && await store.RequeueInterruptedJobAsync(job.Id, CancellationToken.None).ConfigureAwait(false))
                {
                    log?.Invoke($"Stopped {name}; it's queued again.");
                }
                else
                {
                    log?.Invoke($"Cancelled {name}.");
                }
            }
            else
            {
                await store.EndIfRunningAsync(job.Id, JobState.Failed, error ?? "The job ended without recording a result.", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is DbException or DbUpdateException)
        {
            log?.Invoke($"{name}: couldn't record the job's end: {(e.InnerException ?? e).Message}");
        }
        finally
        {
            Wake();
        }
    }

    /// <summary>Whether the process recorded on a running job is still the one running it.</summary>
    public static bool IsRunning(JobRecord job)
    {
        if (job.RunnerPid is not { } pid)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            // A process that started after the job can't be running it: the id was reused. A second allows for rounding.
            return job.StartedAt is not { } started || process.StartTime.ToUniversalTime() <= started.UtcDateTime.AddSeconds(1);
        }
        catch (ArgumentException)
        {
            return false; // No such process.
        }
        catch (InvalidOperationException)
        {
            return false; // It exited.
        }
        catch (Exception e) when (e is Win32Exception or NotSupportedException)
        {
            return true; // Can't tell, so leave the job alone.
        }
    }

    public void Dispose() => _wake.Dispose();

    private sealed record RunningJob(Task Task, CancellationTokenSource Cancel);
}
