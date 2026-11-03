using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Reviewer.Core.Providers;

namespace Reviewer.Providers.GitHub;

/// <summary>
/// GitHub adapter: REST for PRs, reviews and comments; GraphQL for review threads, which REST doesn't expose.
/// Repo listing comes with the desktop app.
/// </summary>
public sealed class GitHubProvider(HttpClient http) : IRepositoryProvider
{
    public static readonly Uri ApiBase = new("https://api.github.com/");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions GraphQlJson = new(JsonSerializerDefaults.Web);

    private const string ThreadsQuery = """
        query($owner: String!, $name: String!, $number: Int!, $after: String) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              reviewThreads(first: 100, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes { id isResolved comments(first: 1) { nodes { databaseId } } }
              }
            }
          }
        }
        """;

    private const string ResolveMutation = """
        mutation($threadId: ID!) {
          resolveReviewThread(input: { threadId: $threadId }) { thread { isResolved } }
        }
        """;

    private const string AutoMergeMutation = """
        mutation($pullRequestId: ID!, $mergeMethod: PullRequestMergeMethod!, $expectedHeadOid: GitObjectID!) {
          enablePullRequestAutoMerge(input: { pullRequestId: $pullRequestId, mergeMethod: $mergeMethod, expectedHeadOid: $expectedHeadOid }) {
            clientMutationId
          }
        }
        """;

    private static readonly HashSet<string> FailedConclusions = ["failure", "cancelled", "timed_out", "action_required", "stale"];

    private readonly HttpClient _http = http;
    private string? _currentUser;

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsRequestChanges: true,
        SupportsThreadResolve: true,
        SupportsSuggestions: true,
        SupportsMergeWhenGreen: true);

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

    /// <summary>
    /// Every repo the token can see, or one owner's: the token owner's own, an organization's (private ones too when
    /// the token is a member), or another user's public ones. Sorted by full name.
    /// </summary>
    public async IAsyncEnumerable<RepoRef> ListAccessibleReposAsync(string? owner, [EnumeratorCancellation] CancellationToken ct)
    {
        var path = owner is null ? "user/repos?per_page=100&sort=full_name"
            : string.Equals(owner, await GetCurrentUserAsync(ct).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase)
                ? "user/repos?affiliation=owner&per_page=100&sort=full_name"
            : await IsOrganizationAsync(owner, ct).ConfigureAwait(false)
                ? $"orgs/{Uri.EscapeDataString(owner)}/repos?per_page=100&sort=full_name"
                : $"users/{Uri.EscapeDataString(owner)}/repos?per_page=100&sort=full_name";
        await foreach (var repo in GetPagesAsync<RepoDto>(path, ct).ConfigureAwait(false))
        {
            if (RepoRef.TryParse(GitHubUrls.GitHubCom, repo.FullName, out var parsed))
            {
                yield return parsed;
            }
        }
    }

    private async Task<bool> IsOrganizationAsync(string owner, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"orgs/{Uri.EscapeDataString(owner)}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<PullRequestList> ListOpenPullRequestsAsync(RepoRef repo, string? etag, CancellationToken ct)
    {
        // Most recently updated first: any change to a PR moves it to the first page, so that page's ETag covers them all.
        string? next = $"{RepoPath(repo)}/pulls?state=open&sort=updated&direction=desc&per_page=100";
        string? newETag = null;
        var result = new List<PullRequestSummary>();
        for (var first = true; next is not null; first = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            if (first && etag is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return PullRequestList.Unchanged(etag);
            }

            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
            if (first)
            {
                newETag = response.Headers.ETag?.ToString();
            }

            foreach (var dto in await ReadAsync<List<PullRequestDto>>(response, ct).ConfigureAwait(false))
            {
                result.Add(new PullRequestSummary(
                    new PullRequestRef(repo, dto.Number),
                    dto.Head.Sha,
                    dto.Base.Ref,
                    dto.User.Login,
                    [.. (dto.Assignees ?? []).Select(u => u.Login)],
                    [.. (dto.RequestedReviewers ?? []).Select(u => u.Login)],
                    [.. (dto.Labels ?? []).Select(l => l.Name)],
                    dto.Draft,
                    dto.Title ?? ""));
            }

            next = NextPage(response);
        }

        return new PullRequestList(false, newETag, result);
    }

    public async Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(PullRequestRef pr, CancellationToken ct)
    {
        var files = new List<ChangedFile>();
        await foreach (var file in GetPagesAsync<PullRequestFileDto>($"{PullPath(pr)}/files?per_page=100", ct).ConfigureAwait(false))
        {
            files.Add(new ChangedFile(file.Filename, file.Additions, file.Deletions));
        }

        return files;
    }

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

        if (result.Count == 0)
        {
            return result;
        }

        // Our comments open their threads, so a thread's first comment identifies it.
        var threads = await GetThreadsAsync(pr, ct).ConfigureAwait(false);
        return [.. result.Select(c => threads.TryGetValue(c.ProviderId, out var t) ? c with { IsResolved = t.IsResolved } : c)];
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
            CommentFormatter.InlineBody(c, "suggestion"),
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

    public async Task ResolveCommentAsync(PullRequestRef pr, PublishedComment comment, string note, CancellationToken ct)
    {
        var threads = await GetThreadsAsync(pr, ct).ConfigureAwait(false);
        if (!threads.TryGetValue(comment.ProviderId, out var thread))
        {
            throw new GitHubApiException(HttpStatusCode.NotFound, $"No review thread starts with comment {comment.ProviderId}.");
        }

        if (thread.IsResolved)
        {
            return; // Someone resolved it already; a late note would only add noise.
        }

        using (var reply = await _http.PostAsJsonAsync(
            $"{PullPath(pr)}/comments/{Uri.EscapeDataString(comment.ProviderId)}/replies", new ReplyRequest(CommentFormatter.Note(note)), Json, ct)
            .ConfigureAwait(false))
        {
            await EnsureSuccessAsync(reply, ct).ConfigureAwait(false);
        }

        await GraphQlAsync<ResolveData>(ResolveMutation, new { threadId = thread.Id }, ct).ConfigureAwait(false);
    }

    /// <summary>Review threads by the REST id of their first comment.</summary>
    private async Task<Dictionary<string, ThreadNode>> GetThreadsAsync(PullRequestRef pr, CancellationToken ct)
    {
        var threads = new Dictionary<string, ThreadNode>(StringComparer.Ordinal);
        string? after = null;
        do
        {
            var data = await GraphQlAsync<ThreadsData>(
                ThreadsQuery, new { owner = pr.Repo.Owner, name = pr.Repo.Name, number = pr.Number, after }, ct).ConfigureAwait(false);
            var connection = data.Repository?.PullRequest?.ReviewThreads
                ?? throw new GitHubApiException(HttpStatusCode.NotFound, $"Pull request {pr.Repo}#{pr.Number} not found.");
            foreach (var thread in connection.Nodes)
            {
                if (thread.Comments.Nodes is [{ DatabaseId: { } id }, ..])
                {
                    threads[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = thread;
                }
            }

            after = connection.PageInfo.HasNextPage ? connection.PageInfo.EndCursor : null;
        }
        while (after is not null);

        return threads;
    }

    // GraphQL reports most failures as 200 with an "errors" array.
    private async Task<T> GraphQlAsync<T>(string query, object variables, CancellationToken ct)
        where T : class
    {
        using var response = await _http.PostAsJsonAsync("graphql", new GraphQlRequest(query, variables), GraphQlJson, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<GraphQlResponse<T>>(GraphQlJson, ct).ConfigureAwait(false);
        if (result?.Errors is { Count: > 0 } errors)
        {
            throw new GitHubApiException(HttpStatusCode.OK, string.Join("; ", errors.Select(e => e.Message)));
        }

        return result?.Data ?? throw new GitHubApiException(response.StatusCode, "Empty GraphQL response.");
    }

    public async Task MergeAsync(PullRequestRef pr, MergeOptions options, CancellationToken ct)
    {
        if (options.WhenGreen)
        {
            // Auto-merge waits for the checks branch protection requires, and needs "Allow auto-merge" on the repo.
            var dto = await GetAsync<PullRequestDto>(PullPath(pr), ct).ConfigureAwait(false);
            var id = dto.NodeId ?? throw new GitHubApiException(HttpStatusCode.NotFound, $"Pull request {pr.Repo}#{pr.Number} has no node id.");
            await GraphQlAsync<AutoMergeData>(
                AutoMergeMutation,
                new { pullRequestId = id, mergeMethod = options.Method.ToString().ToUpperInvariant(), expectedHeadOid = options.ExpectedHeadSha },
                ct).ConfigureAwait(false);
            return;
        }

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
