using Reviewer.Core.Providers;
using Reviewer.Engine.Config;
using Reviewer.Engine.State;

namespace Reviewer.Engine;

/// <summary>A review someone asked for, from <c>reviewer queue add</c> or the local API.</summary>
public static class ManualReview
{
    /// <summary>Queues a review of <paramref name="pr"/> to start now, or merges it into one already queued.</summary>
    /// <param name="agent">An agent to use instead of the configured one; its own settings apply.</param>
    /// <exception cref="ConfigException">The config has no account for the PR, or the agent isn't available.</exception>
    public static async Task<EnqueueResult> QueueAsync(
        ReviewStore store, ReviewerConfig config, PullRequestRef pr, string? agent, bool dryRun, bool full, TimeProvider time, CancellationToken ct)
    {
        var effective = config.For(pr.Repo, agent);
        if (effective.AgentId != AgentsConfig.ClaudeCodeId)
        {
            throw new ConfigException([$"Agent '{effective.AgentId}' is not available yet; use claude-code."]);
        }

        return await store.EnqueueAsync(
            new JobRecord
            {
                Id = ReviewContextBuilder.JobId(pr, time.GetUtcNow()),
                Host = ReviewStore.Host(pr.Repo),
                Repo = pr.Repo.ToString(),
                Number = pr.Number,
                ConfigHash = effective.Hash,
                AgentId = effective.AgentId,
                AppVersion = ReviewStore.AppVersion,
                DryRun = dryRun,
                Full = full,
            },
            ct).ConfigureAwait(false);
    }
}
