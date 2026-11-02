using System.Text.Json;
using System.Text.Json.Serialization;
using Reviewer.Core.Findings;
using Reviewer.Core.Providers;
using Reviewer.Engine;
using Reviewer.Engine.State;

namespace Reviewer.Api;

/// <summary>The engine process behind the API.</summary>
/// <param name="ProcessId">Also what <see cref="JobDto.RunnerPid"/> holds for the jobs this engine runs.</param>
public sealed record EngineStatus(string Version, int ProcessId, DateTimeOffset StartedAt, string DataFolder);

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
        j.Id, j.Host, j.Repo, j.Number, j.State, j.AgentId, j.DryRun, j.Full, j.Published, j.Decision, j.HeadSha, j.BaseSha,
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
public sealed record JobDetailsDto(JobDto Job, IReadOnlyList<FindingDto> Findings)
{
    public static JobDetailsDto From(JobDetails d) => new(JobDto.From(d.Job), [.. d.Findings.Select(FindingDto.From)]);
}

/// <summary>The PR's last published review: what the next one starts from.</summary>
public sealed record LastReviewDto(string HeadSha, string BaseSha, DateTimeOffset ReviewedAt, string JobId);

/// <summary>A finding posted on the PR that no later review found fixed and nobody resolved.</summary>
/// <param name="HeadSha">The head it was reported against; its lines refer to that commit.</param>
public sealed record OpenFindingDto(Finding Finding, string Fingerprint, string HeadSha, bool Blocking);

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

/// <summary>JSON as the API reads and writes it: camelCase keys, kebab-case enum values as in <c>config.yaml</c>.</summary>
public static class ApiJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    internal static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}
