using Reviewer.Core.Findings;

namespace Reviewer.Core.Agents;

/// <summary>An AI backend. It only reports findings; it never acts on the PR.</summary>
public interface IReviewAgent
{
    string Id { get; }                       // "claude-code", "codex", "anthropic-api"...
    AgentCapabilities Capabilities { get; }  // NeedsWorktree, MaxConcurrency, SupportsSchemaFlag
    Task<AgentHealth> CheckAsync(CancellationToken ct);  // installed? logged in? version?
    Task<FindingsReport> ReviewAsync(ReviewContext context, CancellationToken ct);
}

public sealed record AgentCapabilities(bool NeedsWorktree, int MaxConcurrency, bool SupportsSchemaFlag);

public sealed record AgentHealth(bool IsInstalled, bool IsLoggedIn, string? Version, string? Problem = null)
{
    public bool IsReady => IsInstalled && IsLoggedIn && Problem is null;
}

/// <param name="WorktreePath">PR head checkout; the agent's working directory.</param>
/// <param name="AttemptFolder">Job attempt folder outside the worktree holding the input files.</param>
public sealed record ReviewContext(string WorktreePath, string AttemptFolder, TimeSpan Timeout);
