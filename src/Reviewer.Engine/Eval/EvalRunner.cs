using System.Diagnostics;
using Reviewer.Core.Agents;
using Reviewer.Core.Findings;
using Reviewer.Core.Policy;
using Reviewer.Core.Providers;
using Reviewer.Git;

namespace Reviewer.Engine.Eval;

/// <summary>A known bug and the finding that reported it, if one did.</summary>
public sealed record BugResult(KnownBug Bug, Finding? CaughtBy)
{
    public bool Caught => CaughtBy is not null;
}

/// <summary>How a review of one case went.</summary>
/// <param name="Findings">What the review would post, after the policy's confidence and path filters.</param>
/// <param name="Dropped">Findings the policy filtered out.</param>
/// <param name="Other">Findings that match no known bug: noise, or real problems the case doesn't list.</param>
/// <param name="AttemptFolder">The agent's inputs and output; null when the review didn't get that far.</param>
/// <param name="Error">Why the case couldn't be reviewed; null when it was.</param>
public sealed record EvalCaseResult(
    EvalCase Case,
    IReadOnlyList<Finding> Findings,
    int Dropped,
    IReadOnlyList<BugResult> Bugs,
    IReadOnlyList<Finding> Other,
    TimeSpan Duration,
    string? AttemptFolder,
    string? Error = null);

/// <summary>
/// Reviews eval cases the way a review of a PR runs, minus the platform: the same context, instructions, agent and
/// policy filter, but nothing is published or recorded in the review history. Each case's commits come from its
/// fixture folders or its remote, into a repository under the eval folder.
/// </summary>
/// <param name="paths">The eval's own data folder (<see cref="ReviewerPaths.Evals"/>): its repositories and job folders.</param>
/// <param name="agentFor">The agent, given the repository the review's worktree comes from.</param>
public sealed class EvalRunner(
    ReviewerPaths paths,
    Func<IGitRepository, IReviewAgent> agentFor,
    ReviewPolicy policy,
    InstructionSettings instructions,
    TimeSpan timeout,
    TimeProvider time)
{
    /// <summary>Lines a finding may be off by and still count as reporting a bug: agents anchor on a nearby line.</summary>
    public const int LineTolerance = 3;

    /// <summary>Fixture cases' repositories are named after the case on this made-up host.</summary>
    private static readonly Uri FixtureHost = new("https://eval.invalid");

    private static readonly ProviderCapabilities Capabilities = new(
        SupportsRequestChanges: true, SupportsThreadResolve: true, SupportsSuggestions: true, SupportsMergeWhenGreen: false);

    /// <summary>Reviews one case. Agent and git failures are the result's <see cref="EvalCaseResult.Error"/>.</summary>
    /// <exception cref="AgentThrottledException">The agent hit a usage limit; later cases would too.</exception>
    public async Task<EvalCaseResult> RunAsync(EvalCase evalCase, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        string? attempt = null;
        try
        {
            var repo = Repo(evalCase);
            var git = await GitRepository.OpenOrInitAsync(paths.Repository(repo), ct).ConfigureAwait(false);
            var (baseSha, headSha) = await CommitsAsync(git, evalCase, ct).ConfigureAwait(false);

            var mergeBase = await git.MergeBaseAsync(baseSha, headSha, ct).ConfigureAwait(false);
            var (delta, current) = await DeltaPlanner.PlanAsync(git, mergeBase, headSha, null, ct).ConfigureAwait(false);
            var snapshot = new PullRequestSnapshot(
                new PullRequestRef(repo, 0), evalCase.Title, evalCase.Description, headSha, baseSha, "main", "eval",
                [], [], [], IsDraft: false, CiState.Unknown, IsMergeable: false);

            var builder = new ReviewContextBuilder(git, paths, time);
            await using var prepared = await builder
                .PrepareAsync(builder.NewJobId(snapshot.Ref), snapshot, new DeltaPlan(delta, current), "reviewer-eval", instructions, timeout, ct)
                .ConfigureAwait(false);
            attempt = prepared.Context.AttemptFolder;
            var report = await agentFor(git).ReviewAsync(prepared.Context, ct).ConfigureAwait(false);

            // What a review in comment mode would post, inline or in the summary.
            var plan = PolicyEngine.Decide(report, snapshot, policy with { Mode = ReviewMode.Comment }, Capabilities, prepared.Scope);
            var findings = plan.InlineFindings.Concat(plan.SummaryFindings).ToList();
            var (bugs, other) = Score(evalCase.Bugs, findings);
            return new EvalCaseResult(evalCase, findings, report.Findings.Count - findings.Count, bugs, other, clock.Elapsed, attempt);
        }
        catch (Exception e) when (e is (AgentException and not AgentThrottledException) or GitException)
        {
            return new EvalCaseResult(evalCase, [], 0, [.. evalCase.Bugs.Select(b => new BugResult(b, null))], [], clock.Elapsed, attempt, e.Message);
        }
    }

    /// <summary>
    /// Which known bugs the findings report, and which findings report none. A finding reports a bug when it's in the
    /// same file and its lines overlap the bug's, give or take <see cref="LineTolerance"/>.
    /// </summary>
    public static (IReadOnlyList<BugResult> Bugs, IReadOnlyList<Finding> Other) Score(IReadOnlyList<KnownBug> bugs, IReadOnlyList<Finding> findings)
    {
        static bool Reports(Finding f, KnownBug b) =>
            string.Equals(Normalize(f.File), b.File, StringComparison.Ordinal)
            && f.StartLine <= b.EndLine + LineTolerance
            && Math.Max(f.EndLine, f.StartLine) >= b.StartLine - LineTolerance;

        var results = bugs
            .Select(b => new BugResult(b, findings
                .Where(f => Reports(f, b))
                .OrderByDescending(f => f.Severity)
                .ThenByDescending(f => f.Confidence)
                .FirstOrDefault()))
            .ToList();
        var other = findings.Where(f => !bugs.Any(b => Reports(f, b))).ToList();
        return (results, other);
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    private static RepoRef Repo(EvalCase evalCase)
    {
        // A remote's repository is shared by its cases, so it's fetched once.
        if (evalCase.Source is RemoteSource remote
            && Uri.TryCreate(remote.Url, UriKind.Absolute, out var url)
            && url.Scheme is "https" or "http"
            && RepoRef.TryParse(new Uri(url.GetLeftPart(UriPartial.Authority)), url.AbsolutePath.Trim('/').RemoveSuffix(".git"), out var repo))
        {
            return repo;
        }

        return new RepoRef(FixtureHost, "eval", evalCase.Id);
    }

    private static async Task<(string Base, string Head)> CommitsAsync(GitRepository git, EvalCase evalCase, CancellationToken ct)
    {
        var refs = $"refs/reviewer/eval/{evalCase.Id}";
        switch (evalCase.Source)
        {
            case RemoteSource remote:
                if (!await git.HasCommitAsync(remote.BaseSha, ct).ConfigureAwait(false) || !await git.HasCommitAsync(remote.HeadSha, ct).ConfigureAwait(false))
                {
                    await git.FetchAsync(remote.Url, [$"{remote.BaseSha}:{refs}/base", $"{remote.HeadSha}:{refs}/head"], null, ct).ConfigureAwait(false);
                }

                return (remote.BaseSha, remote.HeadSha);

            case FixtureSource fixture:
                var empty = fixture.Before is null ? Directory.CreateTempSubdirectory("reviewer-eval-").FullName : null;
                try
                {
                    var baseSha = await git.CommitDirectoryAsync(fixture.Before ?? empty!, null, "Before the change", ct).ConfigureAwait(false);
                    var headSha = await git.CommitDirectoryAsync(fixture.After, baseSha, evalCase.Title, ct).ConfigureAwait(false);
                    await git.UpdateRefAsync($"{refs}/base", baseSha, ct).ConfigureAwait(false);
                    await git.UpdateRefAsync($"{refs}/head", headSha, ct).ConfigureAwait(false);
                    return (baseSha, headSha);
                }
                finally
                {
                    if (empty is not null)
                    {
                        Directory.Delete(empty);
                    }
                }

            default:
                throw new InvalidOperationException($"Unknown eval source {evalCase.Source}.");
        }
    }
}

internal static class StringExtensions
{
    public static string RemoveSuffix(this string text, string suffix) =>
        text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? text[..^suffix.Length] : text;
}
