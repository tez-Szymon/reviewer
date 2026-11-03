using System.Globalization;
using Reviewer.Agents.Cli;
using Reviewer.Core.Providers;
using Reviewer.Engine;
using Reviewer.Engine.Config;
using Reviewer.Engine.State;
using Reviewer.Git;

namespace Reviewer.Cli;

/// <summary>A review can't be set up: an agent that isn't available yet, or no token.</summary>
internal sealed class ReviewSetupException(string message) : Exception(message);

/// <summary>A runner and request for one PR. Disposing it releases the account's connection.</summary>
internal sealed class PreparedRun(ReviewRunner runner, ReviewRequest request, AccountConnection connection) : IDisposable
{
    public ReviewRunner Runner { get; } = runner;

    public ReviewRequest Request { get; } = request;

    public void Dispose() => connection.Dispose();
}

/// <summary>Builds a review of one PR from its effective config, for <c>reviewer review</c> and the queue alike.</summary>
internal sealed class ReviewFactory(ReviewerPaths paths, IProcessRunner processes, ReviewStore store, string home)
{
    /// <param name="dryRun">Can turn a dry run on, never off: a repo set to dry run stays unpublished.</param>
    /// <exception cref="ReviewSetupException">The review can't run with this config.</exception>
    public async Task<PreparedRun> CreateAsync(PullRequestRef pr, EffectiveConfig effective, bool dryRun, bool full, CancellationToken ct)
    {
        if (effective.AgentId != AgentsConfig.ClaudeCodeId)
        {
            throw new ReviewSetupException($"Agent '{effective.AgentId}' is not available yet; use claude-code.");
        }

        AccountConnection connection;
        try
        {
            connection = await AccountConnection.OpenAsync(processes, effective.Account, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException e)
        {
            throw new ReviewSetupException(e.Message);
        }

        try
        {
            var git = await GitRepository.OpenOrInitAsync(paths.Repository(pr.Repo), ct).ConfigureAwait(false);
            var runner = new ReviewRunner(
                connection.Provider,
                new ReviewContextBuilder(git, paths, TimeProvider.System),
                new ClaudeCodeAgent(new ClaudeCodeOptions(Model: effective.Agent.Model), processes),
                store);
            var policy = effective.Policy.ToReviewPolicy();
            var request = new ReviewRequest(
                pr,
                connection.Source(pr),
                policy with { DryRun = policy.DryRun || dryRun },
                await effective.Policy.Instructions.LoadAsync(paths, home, ct).ConfigureAwait(false),
                effective.Agent.Timeout,
                effective.Hash,
                full);
            return new PreparedRun(runner, request, connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Runs queued jobs for <c>reviewer serve</c>. The config is read again for every job, so edits apply from the next
/// one; the agent's concurrency follows the last config that loaded.
/// </summary>
internal sealed class QueueLauncher(ReviewerPaths paths, ReviewFactory factory, ReviewerConfig config, Action<string> log) : IJobLauncher
{
    private volatile ReviewerConfig _config = config;

    public int MaxConcurrency(string agentId) =>
        AgentsConfig.Known.Contains(agentId) ? _config.Agents.Get(agentId).MaxConcurrency : 1;

    public async Task RunAsync(JobRecord job, CancellationToken ct)
    {
        _config = await ConfigLoader.LoadAsync(paths.Config, ct).ConfigureAwait(false);
        if (!RepoRef.TryParse(new Uri(job.Host), job.Repo, out var repo))
        {
            throw new ReviewSetupException($"'{job.Repo}' is not a repo path such as owner/name.");
        }

        var pr = new PullRequestRef(repo, job.Number);
        // The job keeps the agent it was queued with; that agent's own settings apply, as with --agent.
        using var run = await factory.CreateAsync(pr, _config.For(pr.Repo, job.AgentId), job.DryRun, job.Full, ct).ConfigureAwait(false);
        var outcome = await run.Runner.RunQueuedAsync(job.Id, run.Request, ct).ConfigureAwait(false);
        log($"Finished {job.Repo}#{job.Number} ({job.Id}): {Describe(outcome)}.");
        foreach (var warning in outcome.Warnings)
        {
            log($"  warning: {warning}");
        }
    }

    private static string Describe(ReviewOutcome outcome)
    {
        if (outcome.Skipped)
        {
            return "nothing new";
        }

        var plan = outcome.Plan;
        var decision = plan.Decision switch
        {
            ReviewDecision.Approve => "approved",
            ReviewDecision.RequestChanges => "requested changes",
            _ => "commented",
        };
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(outcome.Published ? decision : $"dry run ({decision})")}, {plan.InlineFindings.Count} inline, {plan.SummaryFindings.Count} in summary");
    }
}
