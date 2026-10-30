using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Reviewer.Core.Providers;

namespace Reviewer.Providers.GitHub;

/// <summary>
/// GitHub REST adapter. M1 scope: current user, snapshot, own comments, publish review, merge.
/// Repo listing, candidate polling and thread resolution come with the engine in M2.
/// </summary>
public sealed class GitHubProvider(HttpClient http) : IRepositoryProvider
{
    public static readonly Uri ApiBase = new("https://api.github.com/");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly HashSet<string> FailedConclusions = ["failure", "cancelled", "timed_out", "action_required", "stale"];

    private readonly HttpClient _http = http;
    private string? _currentUser;

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsRequestChanges: true,
        SupportsThreadResolve: true,
        SupportsSuggestions: true,
        SupportsMergeWhenGreen: false);

    /// <summary>An <see cref="HttpClient"/> for github.com with the headers the REST API expects.</summary>
    public static HttpClient CreateHttpClient(string token, HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.BaseAddress = ApiBase;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("reviewer");
        return client;
    }

    public async Task<string> GetCurrentUserAsync(CancellationToken ct) =>
        _currentUser ??= (await GetAsync<UserDto>("user", ct).ConfigureAwait(false)).Login;

    public IAsyncEnumerable<RepoRef> ListAccessibleReposAsync(string? owner, CancellationToken ct) =>
        throw new NotImplementedException("Repo browsing comes with M2.");

    public IAsyncEnumerable<PullRequestRef> ListCandidatesAsync(RepoRef repo, TriggerFilter filter, CancellationToken ct) =>
        throw new NotImplementedException("Polling comes with M2.");

    public async Task<PullRequestSnapshot> GetSnapshotAsync(PullRequestRef pr, CancellationToken ct)
    {
        var dto = await GetAsync<PullRequestDto>(PullPath(pr), ct).ConfigureAwait(false);
        var ci = await GetCiStateAsync(pr.Repo, dto.Head.Sha, ct).ConfigureAwait(false);
        return new PullRequestSnapshot(
            pr,
            dto.Title ?? "",
            dto.Body ?? "",
            dto.Head.Sha,
            dto.Base.Sha,
            dto.Base.Ref,
            dto.User.Login,
            [.. (dto.Assignees ?? []).Select(u => u.Login)],
            [.. (dto.RequestedReviewers ?? []).Select(u => u.Login)],
            [.. (dto.Labels ?? []).Select(l => l.Name)],
            dto.Draft,
            ci,
            // null while GitHub is still computing mergeability: treat as not mergeable.
            dto.Mergeable ?? false);
    }

    public async Task<IReadOnlyList<PublishedComment>> GetOwnCommentsAsync(PullRequestRef pr, CancellationToken ct)
    {
        var me = await GetCurrentUserAsync(ct).ConfigureAwait(false);
        var result = new List<PublishedComment>();
        await foreach (var comment in GetPagesAsync<ReviewCommentDto>($"{PullPath(pr)}/comments?per_page=100", ct).ConfigureAwait(false))
        {
            if (string.Equals(comment.User?.Login, me, StringComparison.OrdinalIgnoreCase)
                && CommentFormatter.ParseFingerprint(comment.Body) is { } fingerprint)
            {
                result.Add(new PublishedComment(comment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), fingerprint, comment.Path, comment.Line));
            }
        }

        return result;
    }

    public async Task PublishReviewAsync(PullRequestRef pr, ReviewResult result, CancellationToken ct)
    {
        var reviewEvent = result.Decision switch
        {
            ReviewDecision.None => "COMMENT",
            ReviewDecision.Approve => "APPROVE",
            ReviewDecision.RequestChanges => "REQUEST_CHANGES",
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Decision, "Unknown decision."),
        };
        var comments = result.Comments.Select(c => new ReviewCommentRequest(
            c.Finding.File,
            CommentFormatter.InlineBody(c, Capabilities.SupportsSuggestions),
            c.Finding.EndLine,
            "RIGHT",
            c.Finding.StartLine < c.Finding.EndLine ? c.Finding.StartLine : null,
            c.Finding.StartLine < c.Finding.EndLine ? "RIGHT" : null)).ToList();

        var path = $"{PullPath(pr)}/reviews";
        var request = new ReviewRequest(result.HeadSha, CommentFormatter.ReviewBody(result.Summary, result.SummaryFindings), reviewEvent, comments);
        using var response = await _http.PostAsJsonAsync(path, request, Json, ct).ConfigureAwait(false);

        // GitHub rejects the whole review when any comment lands outside the PR diff. Keep the review and its
        // decision, and list the findings in the body instead.
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity && comments.Count > 0)
        {
            var listed = result.Comments.Select(c => c.Finding).Concat(result.SummaryFindings).ToList();
            var fallback = request with { Body = CommentFormatter.ReviewBody(result.Summary, listed), Comments = [] };
            using var retry = await _http.PostAsJsonAsync(path, fallback, Json, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(retry, ct).ConfigureAwait(false);
            return;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    public Task ResolveCommentAsync(PullRequestRef pr, PublishedComment comment, CancellationToken ct) =>
        throw new NotImplementedException("Thread resolution comes with re-review in M2.");

    public async Task MergeAsync(PullRequestRef pr, MergeOptions options, CancellationToken ct)
    {
        // GitHub refuses the merge with 409 if the head no longer matches the reviewed SHA.
        var request = new MergeRequest(options.Method.ToString().ToLowerInvariant(), options.ExpectedHeadSha);
        using var response = await _http.PutAsJsonAsync($"{PullPath(pr)}/merge", request, Json, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    private async Task<CiState> GetCiStateAsync(RepoRef repo, string sha, CancellationToken ct)
    {
        var status = await GetAsync<CombinedStatusDto>($"{RepoPath(repo)}/commits/{sha}/status", ct).ConfigureAwait(false);
        var states = new List<CiState>();
        if (status.TotalCount > 0)
        {
            states.Add(status.State switch
            {
                "success" => CiState.Success,
                "pending" => CiState.Pending,
                _ => CiState.Failure,
            });
        }

        await foreach (var page in GetCheckRunPagesAsync(repo, sha, ct).ConfigureAwait(false))
        {
            states.AddRange(page.Select(run =>
                run.Status != "completed" ? CiState.Pending
                : run.Conclusion is { } c && FailedConclusions.Contains(c) ? CiState.Failure
                : CiState.Success));
        }

        return states.Count == 0 ? CiState.Unknown
            : states.Contains(CiState.Failure) ? CiState.Failure
            : states.Contains(CiState.Pending) ? CiState.Pending
            : CiState.Success;
    }

    private async IAsyncEnumerable<IReadOnlyList<CheckRunDto>> GetCheckRunPagesAsync(
        RepoRef repo, string sha, [EnumeratorCancellation] CancellationToken ct)
    {
        string? next = $"{RepoPath(repo)}/commits/{sha}/check-runs?per_page=100";
        while (next is not null)
        {
            using var response = await _http.GetAsync(next, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            yield return (await ReadAsync<CheckRunsDto>(response, ct).ConfigureAwait(false)).CheckRuns;
            next = NextPage(response);
        }
    }

    private async IAsyncEnumerable<T> GetPagesAsync<T>(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        string? next = path;
        while (next is not null)
        {
            using var response = await _http.GetAsync(next, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            foreach (var item in await ReadAsync<List<T>>(response, ct).ConfigureAwait(false))
            {
                yield return item;
            }

            next = NextPage(response);
        }
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return await ReadAsync<T>(response, ct).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
        ?? throw new GitHubApiException(response.StatusCode, "Empty response body.");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string message;
        try
        {
            message = (await response.Content.ReadFromJsonAsync<ErrorDto>(Json, ct).ConfigureAwait(false))?.Message ?? "";
        }
        catch (JsonException)
        {
            message = "";
        }

        throw new GitHubApiException(response.StatusCode, message);
    }

    // Link: <https://api.github.com/...&page=2>; rel="next", <...>; rel="last"
    private static string? NextPage(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Link", out var values)
            ? values.SelectMany(v => v.Split(','))
                .Select(part => part.Split(';'))
                .Where(p => p.Length == 2 && p[1].Trim() == "rel=\"next\"")
                .Select(p => p[0].Trim().TrimStart('<').TrimEnd('>'))
                .FirstOrDefault()
            : null;

    private static string RepoPath(RepoRef repo) =>
        $"repos/{Uri.EscapeDataString(repo.Owner)}/{Uri.EscapeDataString(repo.Name)}";

    private static string PullPath(PullRequestRef pr) =>
        $"{RepoPath(pr.Repo)}/pulls/{pr.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
