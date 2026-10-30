using Reviewer.Core.Providers;

namespace Reviewer.Engine;

/// <summary>Where the reviewer keeps its state, by default under <c>~/.reviewer</c>.</summary>
public sealed record ReviewerPaths(string Root)
{
    /// <summary><c>REVIEWER_HOME</c> if set, otherwise <c>~/.reviewer</c>.</summary>
    public static ReviewerPaths Default { get; } = new(
        Environment.GetEnvironmentVariable("REVIEWER_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".reviewer"));

    /// <summary><c>config.yaml</c>; without it the built-in defaults apply.</summary>
    public string Config => Path.Combine(Root, Engine.Config.ConfigLoader.FileName);

    /// <summary>Per-job folders: worktree plus numbered attempt folders.</summary>
    public string Jobs => Path.Combine(Root, "jobs");

    /// <summary>The engine's bare repository for <paramref name="repo"/>, the source of job worktrees.</summary>
    public string Repository(RepoRef repo) => Path.Combine(Root, "repos", repo.Host.Host, repo.Owner, repo.Name + ".git");
}
