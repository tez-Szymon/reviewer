using System.Net;
using Reviewer.Engine.Config;

namespace Reviewer.Api;

/// <summary>What clients ask of the engine's local API; <see cref="EngineClient"/> asks it over the socket.</summary>
/// <remarks>Methods throw <see cref="EngineApiException"/> when the engine refuses, and <see cref="HttpRequestException"/> when none is listening.</remarks>
public interface IEngineClient
{
    /// <summary>The engine behind the socket, or null when none is listening.</summary>
    Task<EngineStatus?> GetStatusAsync(CancellationToken ct);

    Task<List<AgentStatus>> GetAgentsAsync(CancellationToken ct);

    Task<List<AccountDto>> GetAccountsAsync(CancellationToken ct);

    Task<AccountStatus> GetAccountStatusAsync(string id, CancellationToken ct);

    /// <param name="owner">A user, organization or GitLab group whose repos to list; null lists all the account can see.</param>
    Task<List<RepoChoice>> ListAccountReposAsync(string id, string? owner, CancellationToken ct);

    /// <summary><c>config.yaml</c> as written, with the hash an edit must name.</summary>
    Task<ConfigText> GetConfigFileAsync(CancellationToken ct);

    /// <summary>Applies the edits, all or none, and returns the saved file.</summary>
    /// <remarks>
    /// Throws <see cref="EngineApiException"/> with <see cref="HttpStatusCode.Conflict"/> when the file changed since it
    /// was read, and <see cref="HttpStatusCode.UnprocessableEntity"/> with <see cref="EngineApiException.Errors"/> when
    /// the result would be invalid.
    /// </remarks>
    Task<ConfigText> EditConfigAsync(ConfigEdits edits, CancellationToken ct);

    Task<List<JobDto>> GetQueueAsync(CancellationToken ct);

    Task<List<JobSummaryDto>> ListJobsAsync(string? repo, int? number, int limit, CancellationToken ct);

    Task<JobDetailsDto?> GetJobAsync(string id, CancellationToken ct);

    Task<CancelResult> CancelJobAsync(string id, CancellationToken ct);

    Task<JobDto> RetryJobAsync(string id, CancellationToken ct);

    Task<QueueResult> QueueReviewAsync(QueueRequest request, CancellationToken ct);

    Task<List<WatchedPullRequestDto>> ListPullsAsync(string? repo, CancellationToken ct);

    Task<PullRequestDto> GetPullAsync(string? host, string repo, int number, CancellationToken ct);

    Task ShutdownAsync(CancellationToken ct);

    /// <summary>Job changes as the engine streams them, until cancelled or the engine stops.</summary>
    /// <param name="after">A <see cref="JobDto.Version"/> to resume after; null starts from now.</param>
    /// <param name="opened">Called once the stream is open, when lists can be loaded without missing a change.</param>
    IAsyncEnumerable<JobDto> WatchJobsAsync(long? after, Action? opened, CancellationToken ct);
}
