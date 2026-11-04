using System.Text.Json;
using System.Text.Json.Serialization;
using Reviewer.Core.Findings;
using Reviewer.Core.Policy;
using Reviewer.Core.Providers;
using Reviewer.Engine;
using Reviewer.Engine.Config;
using Reviewer.Engine.State;

namespace Reviewer.Api;

/// <summary>The engine process behind the API.</summary>
/// <param name="ProcessId">Also what <see cref="JobDto.RunnerPid"/> holds for the jobs this engine runs.</param>
public sealed record EngineStatus(string Version, int ProcessId, DateTimeOffset StartedAt, string DataFolder);

/// <summary>An account from the config.</summary>
/// <param name="Token">The token's reference (<c>env:NAME</c>, <c>keychain:SERVICE/ACCOUNT</c>), never the token; null uses the provider's defaults.</param>
public sealed record AccountDto(string Id, string Provider, Uri Host, string? Token);

/// <summary>Whether the account's token works, and whose it is.</summary>
public sealed record AccountStatus(string Id, bool SignedIn, string? Login, string? Problem);

/// <summary>A repo the account can see, for picking repos to watch.</summary>
/// <param name="Repo">The path as <c>repos</c> takes it: <c>owner/name</c>, or a GitLab project's full path.</param>
/// <param name="Watched">The config already has an entry for it under this account.</param>
public sealed record RepoChoice(string Repo, bool Watched);

/// <summary>Whether an agent can run reviews now.</summary>
public sealed record AgentStatus(string Id, bool Ready, bool Installed, bool LoggedIn, string? Version, string? Problem);

/// <summary>A job as the API and its events show it.</summary>
/// <param name="Version">
/// Grows with every change to any job; of two copies of a job, the higher version is the newer. Also the event id.
/// </param>
public sealed record JobDto(
    string Id,
    string Host,
    string Repo,
    int Number,
    JobState State,
    string AgentId,
    bool DryRun,
    bool Full,
    bool Published,
    ReviewDecision? Decision,
    MergeIntent? Merge,
    string? HeadSha,
    string? BaseSha,
    DeltaKind? Delta,
    string? DeltaFrom,
    string? Summary,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? RetryAt,
    DateTimeOffset? NotBefore,
    bool CancelRequested,
    int? RunnerPid,
    string? AttemptFolder,
    long Version)
{
    public static JobDto From(JobRecord j) => new(
        j.Id, j.Host, j.Repo, j.Number, j.State, j.AgentId, j.DryRun, j.Full, j.Published, j.Decision, j.Merge, j.HeadSha, j.BaseSha,
        j.Delta, j.DeltaFrom, j.Summary, j.Error, j.CreatedAt, j.StartedAt, j.FinishedAt, j.RetryAt, j.NotBefore,
        j.CancelRequested, j.RunnerPid, j.AttemptFolder, j.Version);
}

/// <summary>A job for listings, with how many findings went inline and to the summary, and how many blocked.</summary>
public sealed record JobSummaryDto(JobDto Job, int Inline, int SummaryOnly, int Blocking)
{
    public static JobSummaryDto From(JobSummary s) => new(JobDto.From(s.Job), s.Inline, s.SummaryOnly, s.Blocking);
}

/// <summary>A finding as the agent reported it, with what the policy engine did with it.</summary>
public sealed record FindingDto(
    string Id,
    Severity Severity,
    Category Category,
    double Confidence,
    string File,
    int StartLine,
    int EndLine,
    string Title,
    string Body,
    string? Suggestion,
    FindingDisposition Disposition,
    bool Blocking,
    string? Fingerprint,
    string? ResolvedByJobId)
{
    public static FindingDto From(FindingRecord f) => new(
        f.FindingId, f.Severity, f.Category, f.Confidence, f.File, f.StartLine, f.EndLine, f.Title, f.Body, f.Suggestion,
        f.Disposition, f.Blocking, f.Fingerprint, f.ResolvedByJobId);
}

/// <summary>A job with its findings, in report order.</summary>
/// <param name="Reasons">Why the policy decided as it did, from the attempt's plan; empty before the agent finished.</param>
public sealed record JobDetailsDto(JobDto Job, IReadOnlyList<FindingDto> Findings, IReadOnlyList<string> Reasons)
{
    public static JobDetailsDto From(JobDetails d, IReadOnlyList<string> reasons) => new(JobDto.From(d.Job), [.. d.Findings.Select(FindingDto.From)], reasons);
}

/// <summary>The PR's last published review: what the next one starts from.</summary>
public sealed record LastReviewDto(string HeadSha, string BaseSha, DateTimeOffset ReviewedAt, string JobId);

/// <summary>A finding posted on the PR that no later review found fixed and nobody resolved.</summary>
/// <param name="HeadSha">The head it was reported against; its lines refer to that commit.</param>
public sealed record OpenFindingDto(Finding Finding, string Fingerprint, string HeadSha, bool Blocking);

/// <summary>
/// An open PR in a watched repo as the scheduler last saw it, with its last published review and newest job. A last
/// review whose head isn't <paramref name="HeadSha"/> means commits arrived since.
/// </summary>
/// <param name="Title">Untrusted author text.</param>
/// <param name="Matched">An initial trigger matched, so it's reviewed when it changes.</param>
public sealed record WatchedPullRequestDto(
    string Host,
    string Repo,
    int Number,
    string Title,
    string Author,
    string HeadSha,
    bool Matched,
    bool Assigned,
    bool ReviewRequested,
    DateTimeOffset SeenAt,
    LastReviewDto? LastReview,
    JobSummaryDto? LatestJob)
{
    public static WatchedPullRequestDto From(WatchedPullRequest w) => new(
        w.Seen.Host, w.Seen.Repo, w.Seen.Number, w.Seen.Title, w.Seen.Author, w.Seen.HeadSha, w.Seen.Matched, w.Seen.Assigned,
        w.Seen.ReviewRequested, w.Seen.SeenAt,
        w.LastReview is { } r ? new LastReviewDto(r.LastReviewedHead, r.LastReviewedBase, r.LastReviewedAt, r.LastJobId) : null,
        w.LatestJob is { } j ? JobSummaryDto.From(j) : null);
}

/// <summary>A PR's review state: its last published review, what's still open on it, and its jobs, newest first.</summary>
public sealed record PullRequestDto(
    string Host, string Repo, int Number, LastReviewDto? LastReview, IReadOnlyList<OpenFindingDto> OpenFindings, IReadOnlyList<JobSummaryDto> Jobs);

/// <summary>Asks for a review of the PR at <paramref name="Url"/>, as <c>reviewer queue add</c> does.</summary>
/// <param name="Agent">An agent to use instead of the configured one.</param>
public sealed record QueueRequest(string Url, bool DryRun = false, bool Full = false, string? Agent = null);

/// <param name="Added">False when the PR already had a queued job and the request was merged into it.</param>
public sealed record QueueResult(string JobId, bool Added);

/// <param name="State">
/// <see cref="JobState.Cancelled"/>, or <see cref="JobState.Running"/> when the engine was asked to stop the job.
/// </param>
public sealed record CancelResult(string JobId, JobState State);

public sealed record ConfigValidation(bool Valid, IReadOnlyList<string> Errors);

/// <summary>A new <c>config.yaml</c>, to replace the one with <paramref name="BaseHash"/>.</summary>
public sealed record ConfigSave(string Yaml, string BaseHash);

/// <summary>Edits to apply in order to the <c>config.yaml</c> with <paramref name="BaseHash"/>; all or none are saved.</summary>
public sealed record ConfigEdits(string BaseHash, IReadOnlyList<ConfigEdit> Edits);

/// <summary>JSON as the API reads and writes it: camelCase keys, kebab-case enum values as in <c>config.yaml</c>.</summary>
public static class ApiJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Adds the API's conventions to <paramref name="options"/>, as the engine configures its own.</summary>
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}
