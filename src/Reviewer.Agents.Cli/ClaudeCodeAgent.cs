using Reviewer.Core.Agents;
using Reviewer.Core.Findings;

namespace Reviewer.Agents.Cli;

/// <summary>Runs the Claude Code CLI headless in the PR worktree, using the job-folder file contract.</summary>
public sealed class ClaudeCodeAgent : IReviewAgent
{
    public string Id => "claude-code";

    public AgentCapabilities Capabilities { get; } = new(NeedsWorktree: true, MaxConcurrency: 1, SupportsSchemaFlag: true);

    public Task<AgentHealth> CheckAsync(CancellationToken ct) => throw new NotImplementedException();

    public Task<FindingsReport> ReviewAsync(ReviewContext context, CancellationToken ct) => throw new NotImplementedException();
}
