using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reviewer.Core.Providers;
using Reviewer.Engine.Config;
using Reviewer.Engine.State;

namespace Reviewer.Engine;

/// <summary>What the scheduler polls with; the host supplies config and providers.</summary>
public interface IPollSource
{
    /// <summary>The config to poll with, read again before each pass so edits apply.</summary>
    /// <exception cref="ConfigException">The file is invalid; the scheduler keeps the last config that loaded.</exception>
    Task<ReviewerConfig> LoadConfigAsync(CancellationToken ct);

    /// <summary>The provider for an account, kept for the engine's lifetime.</summary>
    /// <exception cref="InvalidOperationException">The account can't be polled, e.g. it has no token.</exception>
    Task<IRepositoryProvider> GetProviderAsync(AccountConfig account, CancellationToken ct);
}

/// <summary>
/// Polls the repos under <c>repos</c> in the config and queues reviews when <see cref="TriggerRules"/> say so, until
/// stopped. Each repo is polled every <see cref="TriggerConfig.PollSeconds"/>; an unchanged PR list (same ETag) costs
/// one request and no rate limit.
/// </summary>
/// <remarks>
/// A PR is acted on only while the filters pass (<see cref="TriggerRules.PassesFilters"/>), and what's recorded as seen
/// is its state on the last such poll: a push to a draft is noticed when it's marked ready. The list's ETag is saved
/// only after every PR in it was handled, so a failure part-way reads the list again on the next poll.
/// </remarks>
public sealed class Scheduler(ReviewStore store, IPollSource source, TimeProvider time, Action? queued = null, Action<string>? log = null)
{
    /// <summary>The longest the scheduler sleeps, so a config edit that adds a repo is picked up soon.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    private ReviewerConfig? _config;

    // The last problem logged per repo, so a failure that repeats on every poll is logged once.
    private readonly Dictionary<string, string> _problems = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Polls until <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                TimeSpan wait;
                try
                {
                    wait = await TickAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is DbException or DbUpdateException)
                {
                    log?.Invoke($"Couldn't record a poll: {(e.InnerException ?? e).Message}");
                    wait = MaxWait;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // The queue worker runs beside the scheduler; keep polling rather than stop unnoticed.
                    log?.Invoke($"Polling failed: {e.Message}");
                    wait = MaxWait;
                }

                await Task.Delay(wait, time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>One pass: polls each repo that's due.</summary>
    /// <returns>How long until the next repo is due, at most <see cref="MaxWait"/>.</returns>
    internal async Task<TimeSpan> TickAsync(CancellationToken ct)
    {
        try
        {
            _config = await source.LoadConfigAsync(ct).ConfigureAwait(false);
            Report("config", null);
        }
        catch (ConfigException e)
        {
            Report("config", $"The config is invalid; polling with the last one that loaded. {e.Message}");
        }

        if (_config is not { } config)
        {
            return MaxWait;
        }

        var wait = MaxWait;
        foreach (var repo in config.WatchedRepos())
        {
            EffectiveConfig effective;
            try
            {
                effective = config.For(repo);
            }
            catch (ConfigException e)
            {
                Report(repo.ToString(), e.Message);
                continue;
            }

            var interval = effective.Policy.Triggers.PollInterval;
            var state = await store.GetPollStateAsync(repo, ct).ConfigureAwait(false);
            var dueAt = state is null ? time.GetUtcNow() : state.LastPolledAt + interval;
            if (dueAt <= time.GetUtcNow())
            {
                await PollAsync(repo, effective, state, ct).ConfigureAwait(false);
                dueAt = time.GetUtcNow() + interval;
            }

            var until = dueAt - time.GetUtcNow();
            wait = until < wait ? until : wait;
        }

        return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
    }

    private async Task PollAsync(RepoRef repo, EffectiveConfig effective, PollStateRecord? state, CancellationToken ct)
    {
        var triggers = effective.Policy.Triggers;
        try
        {
            var provider = await source.GetProviderAsync(effective.Account, ct).ConfigureAwait(false);
            var me = await provider.GetCurrentUserAsync(ct).ConfigureAwait(false);
            var rulesHash = RulesHash(triggers, me);
            // New rules or a new token owner can trigger differently on the same list, so read it in full.
            var etag = state?.RulesHash == rulesHash ? state.ETag : null;
            var list = await provider.ListOpenPullRequestsAsync(repo, etag, ct).ConfigureAwait(false);
            if (!list.NotModified)
            {
                await HandleAsync(provider, repo, list.PullRequests, effective, me, ct).ConfigureAwait(false);
            }

            await store.SavePollStateAsync(repo, list.ETag, rulesHash, ct).ConfigureAwait(false);
            Report(repo.ToString(), null);
        }
        // Provider, network and token problems alike; the store's own errors end the pass.
        catch (Exception e) when (e is not (OperationCanceledException or DbException or DbUpdateException))
        {
            Report(repo.ToString(), $"Couldn't poll {repo}: {e.Message}");
            // Wait the interval before trying again; the old ETag and rules stay, so nothing is lost.
            await store.SavePollStateAsync(repo, state?.ETag, state?.RulesHash ?? "", ct).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(
        IRepositoryProvider provider, RepoRef repo, IReadOnlyList<PullRequestSummary> open, EffectiveConfig effective, string me, CancellationToken ct)
    {
        var triggers = effective.Policy.Triggers;
        var seenBefore = await store.GetSeenAsync(repo, ct).ConfigureAwait(false);
        var seenNow = new Dictionary<int, SeenPullRequest>();
        try
        {
            foreach (var pr in open.Where(p => TriggerRules.PassesFilters(p, triggers)))
            {
                var now = TriggerRules.See(pr, triggers, me);
                var before = seenBefore.GetValueOrDefault(pr.Ref.Number);
                if (now != before)
                {
                    await TriggerAsync(provider, pr, now, before, effective, ct).ConfigureAwait(false);
                }

                seenNow[pr.Ref.Number] = now;
            }
        }
        finally
        {
            // A PR whose handling failed keeps its old record, so the next poll handles it again.
            await store.SaveSeenAsync(repo, seenNow, [.. open.Select(p => p.Ref.Number)], ct).ConfigureAwait(false);
        }
    }

    private async Task TriggerAsync(
        IRepositoryProvider provider, PullRequestSummary pr, SeenPullRequest now, SeenPullRequest? before, EffectiveConfig effective, CancellationToken ct)
    {
        var triggers = effective.Policy.Triggers;
        var history = await store.GetReviewHistoryAsync(pr.Ref, pr.HeadSha, effective.Hash, ct).ConfigureAwait(false);
        if (TriggerRules.Decide(now, before, history.ReviewedHead, triggers, time.GetUtcNow()) is not { } decision || history.HeadReviewed)
        {
            return;
        }

        var repo = pr.Ref.Repo;
        var name = $"{repo}#{pr.Ref.Number}";
        if (TriggerRules.RejectChanges(await provider.GetChangedFilesAsync(pr.Ref, ct).ConfigureAwait(false), triggers) is { } reason)
        {
            log?.Invoke($"Not reviewing {name}: {reason}.");
            return;
        }

        var job = new JobRecord
        {
            Id = ReviewContextBuilder.JobId(pr.Ref, time.GetUtcNow()),
            Host = ReviewStore.Host(repo),
            Repo = repo.ToString(),
            Number = pr.Ref.Number,
            ConfigHash = effective.Hash,
            AgentId = effective.AgentId,
            AppVersion = ReviewStore.AppVersion,
            NotBefore = decision.NotBefore,
        };
        var (id, added) = await store.EnqueueAsync(job, ct).ConfigureAwait(false);
        var why = string.Join(", ", decision.Reasons.Select(Describe));
        var when = decision.NotBefore is { } at ? $", from {at.ToLocalTime():HH:mm}" : "";
        log?.Invoke(added ? $"Queued {name} ({why}{when}) as {id}." : $"{name} is already queued as {id} ({why}{when}).");
        queued?.Invoke();
    }

    private static string Describe(TriggerReason reason) => reason switch
    {
        TriggerReason.Initial => "first review",
        TriggerReason.NewCommits => "new commits",
        TriggerReason.Assigned => "assigned",
        TriggerReason.ReviewRequested => "review requested",
        _ => reason.ToString(),
    };

    private static string RulesHash(TriggerConfig triggers, string me)
    {
        var bytes = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { triggers, me = me.ToLowerInvariant() }, ConfigLoader.JsonOptions));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    // Logs a problem when it first appears or changes, and its end.
    private void Report(string key, string? problem)
    {
        if (problem is null)
        {
            if (_problems.Remove(key) && key != "config")
            {
                log?.Invoke($"Polling {key} works again.");
            }
        }
        else if (!_problems.TryGetValue(key, out var last) || last != problem)
        {
            _problems[key] = problem;
            log?.Invoke(problem);
        }
    }
}
