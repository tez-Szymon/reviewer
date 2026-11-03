using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Reviewer.Core.Findings;
using Reviewer.Core.Providers;

namespace Reviewer.Providers.GitLab;

/// <summary>
/// GitLab adapter, for gitlab.com and self-hosted instances. GitLab has no single review object, so a review is its
/// inline comments as diff discussions, then the summary as a note, then the approval. It can't request changes; the
/// policy engine falls back, and an approval this account gave earlier is withdrawn instead.
/// </summary>
public sealed class GitLabProvider(HttpClient http) : IRepositoryProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly HashSet<string> RunningPipelines =
        ["created", "waiting_for_resource", "preparing", "pending", "running", "scheduled", "manual"];

    private static readonly HashSet<string> MergeableStatuses = ["mergeable", "ci_still_running", "ci_must_pass", "not_approved"];

    private readonly HttpClient _http = http;
    private UserDto? _currentUser;

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsRequestChanges: false,
        SupportsThreadResolve: true,
        SupportsSuggestions: true,
        SupportsMergeWhenGreen: true);

    /// <summary>An <see cref="HttpClient"/> for the GitLab instance at <paramref name="host"/>.</summary>
    public static HttpClient CreateHttpClient(Uri host, string token, HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.BaseAddress = GitLabUrls.ApiBase(host);
        client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("reviewer");
        return client;
    }

    public async Task<string> GetCurrentUserAsync(CancellationToken ct) => (await CurrentUserAsync(ct).ConfigureAwait(false)).Username;

    public IAsyncEnumerable<RepoRef> ListAccessibleReposAsync(string? owner, CancellationToken ct) =>
        throw new NotImplementedException("Repo browsing comes with the desktop app.");

    public async Task<PullRequestList> ListOpenPullRequestsAsync(RepoRef repo, string? etag, CancellationToken ct)
    {
        // Most recently updated first, as on GitHub, so the first page's ETag covers every change.
        string? next = $"{ProjectPath(repo)}/merge_requests?state=opened&order_by=updated_at&sort=desc&per_page=100";
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

            foreach (var dto in await ReadAsync<List<MergeRequestDto>>(response, ct).ConfigureAwait(false))
            {
                result.Add(new PullRequestSummary(
                    new PullRequestRef(repo, dto.Iid),
                    dto.Sha,
                    dto.TargetBranch,
                    dto.Author.Username,
                    Usernames(dto.Assignees),
                    Usernames(dto.Reviewers),
                    dto.Labels ?? [],
                    dto.Draft));
            }

            next = NextPage(response);
        }

        return new PullRequestList(false, newETag, result);
    }

    public async Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(PullRequestRef pr, CancellationToken ct)
    {
        var files = new List<ChangedFile>();
        await foreach (var diff in GetPagesAsync<DiffDto>($"{MergeRequestPath(pr)}/diffs?per_page=100", ct).ConfigureAwait(false))
        {
            // GitLab leaves the diff out of files it considers too large, so those count as no lines.
            var lines = DiffLines.Parse(diff.Diff);
            files.Add(new ChangedFile(diff.DeletedFile ? diff.OldPath : diff.NewPath, lines.Additions, lines.Deletions));
        }

        return files;
    }

    public async Task<PullRequestSnapshot> GetSnapshotAsync(PullRequestRef pr, CancellationToken ct)
    {
        var dto = await GetAsync<MergeRequestDto>(MergeRequestPath(pr), ct).ConfigureAwait(false);
        var refs = dto.DiffRefs
            ?? throw new GitLabApiException(HttpStatusCode.Conflict, $"Merge request {pr.Repo}!{pr.Number} has no diff yet; GitLab is still preparing it.");
        return new PullRequestSnapshot(
            pr,
            dto.Title ?? "",
            dto.Description ?? "",
            dto.Sha,
            // The target branch's head, like GitHub's base SHA; the engine works out the merge base itself.
            refs.StartSha,
            dto.TargetBranch,
            dto.Author.Username,
            Usernames(dto.Assignees),
            Usernames(dto.Reviewers),
            dto.Labels ?? [],
            dto.Draft,
            dto.HeadPipeline?.Status switch
            {
                null or "skipped" => CiState.Unknown,
                "success" => CiState.Success,
                { } running when RunningPipelines.Contains(running) => CiState.Pending,
                _ => CiState.Failure,
            },
            // CI and approvals have guards of their own. Anything else, including "checking" while GitLab works it out, is
            // not mergeable yet.
            dto.DetailedMergeStatus is { } detailed ? MergeableStatuses.Contains(detailed) : dto.MergeStatus == "can_be_merged");
    }

    public async Task<IReadOnlyList<PublishedComment>> GetOwnCommentsAsync(PullRequestRef pr, CancellationToken ct)
    {
        var me = await GetCurrentUserAsync(ct).ConfigureAwait(false);
        var result = new List<PublishedComment>();
        await foreach (var discussion in GetPagesAsync<DiscussionDto>($"{MergeRequestPath(pr)}/discussions?per_page=100", ct).ConfigureAwait(false))
        {
            // Our comments open their discussions, so the first note is the one to look at.
            if (discussion.Notes is [var first, ..]
                && !first.System
                && string.Equals(first.Author?.Username, me, StringComparison.OrdinalIgnoreCase)
                && CommentFormatter.ParseFingerprint(first.Body) is { } fingerprint)
            {
                result.Add(new PublishedComment(
                    discussion.Id,
                    fingerprint,
                    first.Position?.NewPath ?? first.Position?.OldPath,
                    first.Position?.NewLine ?? first.Position?.OldLine,
                    first.Resolvable && first.Resolved));
            }
        }

        return result;
    }

    public async Task PublishReviewAsync(PullRequestRef pr, ReviewResult result, CancellationToken ct)
    {
        var listed = new List<Finding>();
        if (result.Comments.Count > 0)
        {
            listed.AddRange(await PostInlineCommentsAsync(pr, result, ct).ConfigureAwait(false));
        }

        listed.AddRange(result.SummaryFindings);
        using (var note = await _http.PostAsJsonAsync(
            $"{MergeRequestPath(pr)}/notes", new NoteRequest(CommentFormatter.ReviewBody(result.Summary, listed)), Json, ct).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(note, ct).ConfigureAwait(false);
        }

        var approved = await IsApprovedByMeAsync(pr, ct).ConfigureAwait(false);
        switch (result.Decision)
        {
            case ReviewDecision.Approve when !approved:
                // GitLab refuses with 409 if the head has moved past the reviewed commit.
                await PostAsync($"{MergeRequestPath(pr)}/approve", new ApproveRequest(result.HeadSha), ct).ConfigureAwait(false);
                break;
            case ReviewDecision.RequestChanges when approved:
            case ReviewDecision.None when approved && result.WithdrawApproval:
                await PostAsync($"{MergeRequestPath(pr)}/unapprove", null, ct).ConfigureAwait(false);
                break;
        }
    }

    public async Task ResolveCommentAsync(PullRequestRef pr, PublishedComment comment, string note, CancellationToken ct)
    {
        var path = $"{MergeRequestPath(pr)}/discussions/{Uri.EscapeDataString(comment.ProviderId)}";
        var discussion = await GetAsync<DiscussionDto>(path, ct).ConfigureAwait(false);
        if (discussion.Notes is [{ Resolved: true }, ..])
        {
            return; // Someone resolved it already; a late note would only add noise.
        }

        await PostAsync($"{path}/notes", new NoteRequest(CommentFormatter.Note(note)), ct).ConfigureAwait(false);
        using var resolve = await _http.PutAsJsonAsync(path, new ResolveRequest(true), Json, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resolve, ct).ConfigureAwait(false);
    }

    public async Task MergeAsync(PullRequestRef pr, MergeOptions options, CancellationToken ct)
    {
        // The merge method is the project's setting; only squashing can be chosen per merge.
        if (options.Method == MergeMethod.Rebase)
        {
            throw new NotSupportedException("GitLab merges with the project's merge method; set it to fast-forward in the project to rebase.");
        }

        // GitLab refuses with 409 if the head no longer matches the reviewed SHA. auto_merge replaced
        // merge_when_pipeline_succeeds in 17.11; older instances only know the latter, newer ones accept both.
        var request = new MergeRequestMerge(options.ExpectedHeadSha, options.Method == MergeMethod.Squash, options.WhenGreen, options.WhenGreen);
        using var response = await _http.PutAsJsonAsync($"{MergeRequestPath(pr)}/merge", request, Json, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Posts each comment as a diff discussion on the MR version at the reviewed head, and returns the findings that
    /// couldn't go inline, for the summary: all of them when that version is gone, otherwise those on lines its diff
    /// doesn't show or that GitLab turned down.
    /// </summary>
    private async Task<List<Finding>> PostInlineCommentsAsync(PullRequestRef pr, ReviewResult result, CancellationToken ct)
    {
        var all = result.Comments.Select(c => c.Finding).ToList();
        var versions = await GetAsync<List<VersionDto>>($"{MergeRequestPath(pr)}/versions", ct).ConfigureAwait(false);
        if (versions.FirstOrDefault(v => v.HeadCommitSha == result.HeadSha) is not { } reviewed)
        {
            return all;
        }

        var version = await GetAsync<VersionDetailsDto>(
            $"{MergeRequestPath(pr)}/versions/{reviewed.Id.ToString(CultureInfo.InvariantCulture)}", ct).ConfigureAwait(false);
        var diffs = version.Diffs.Where(d => !d.DeletedFile).ToDictionary(d => d.NewPath, StringComparer.Ordinal);

        var listed = new List<Finding>();
        foreach (var comment in result.Comments)
        {
            var f = comment.Finding;
            if (!diffs.TryGetValue(f.File, out var diff) || Anchor(f, DiffLines.Parse(diff.Diff)) is not { } anchor)
            {
                listed.Add(f);
                continue;
            }

            var request = new DiscussionRequest(
                CommentFormatter.InlineBody(comment, anchor.Suggestion),
                new PositionRequest(
                    "text", version.BaseCommitSha, version.StartCommitSha, version.HeadCommitSha, diff.OldPath, diff.NewPath, anchor.Line, anchor.OldLine));
            using var response = await _http.PostAsJsonAsync($"{MergeRequestPath(pr)}/discussions", request, Json, ct).ConfigureAwait(false);

            // GitLab checks the position against the diff itself; one it turns down still belongs in the review.
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                listed.Add(f);
                continue;
            }

            await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        }

        return listed;
    }

    /// <summary>
    /// The line to put a finding's comment on, the old line GitLab needs if that line is unchanged, and the suggestion
    /// fence that replaces the finding's lines from there. The last line first, as on GitHub, then the first. Null when
    /// the diff shows neither.
    /// </summary>
    internal static (int Line, int? OldLine, string Suggestion)? Anchor(Finding f, DiffLines diff)
    {
        var span = (f.EndLine - f.StartLine).ToString(CultureInfo.InvariantCulture);
        return diff.TryGetAnchor(f.EndLine, out var oldEnd) ? (f.EndLine, oldEnd, $"suggestion:-{span}+0")
            : diff.TryGetAnchor(f.StartLine, out var oldStart) ? (f.StartLine, oldStart, $"suggestion:-0+{span}")
            : null;
    }

    private async Task<bool> IsApprovedByMeAsync(PullRequestRef pr, CancellationToken ct)
    {
        var me = await CurrentUserAsync(ct).ConfigureAwait(false);
        var approvals = await GetAsync<ApprovalsDto>($"{MergeRequestPath(pr)}/approvals", ct).ConfigureAwait(false);
        return (approvals.ApprovedBy ?? []).Any(a => a.User.Id == me.Id);
    }

    private async Task<UserDto> CurrentUserAsync(CancellationToken ct) =>
        _currentUser ??= await GetAsync<UserDto>("user", ct).ConfigureAwait(false);

    private async Task PostAsync(string path, object? body, CancellationToken ct)
    {
        using var response = body is null
            ? await _http.PostAsync(path, null, ct).ConfigureAwait(false)
            : await _http.PostAsJsonAsync(path, body, Json, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
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
        ?? throw new GitLabApiException(response.StatusCode, "Empty response body.");

    // GitLab's errors are { "message": "..." }, { "message": { "field": ["..."] } } or { "error": "..." }.
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = "";
        try
        {
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
                .ConfigureAwait(false);
            if (body.RootElement.ValueKind == JsonValueKind.Object)
            {
                message = body.RootElement.TryGetProperty("message", out var m) ? (m.ValueKind == JsonValueKind.String ? m.GetString()! : m.GetRawText())
                    : body.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()!
                    : "";
            }
        }
        catch (JsonException)
        {
        }

        throw new GitLabApiException(response.StatusCode, message);
    }

    // Link: <https://gitlab.com/api/v4/...&page=2>; rel="next", <...>; rel="last"
    private static string? NextPage(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Link", out var values)
            ? values.SelectMany(v => v.Split(','))
                .Select(part => part.Split(';'))
                .Where(p => p.Length == 2 && p[1].Trim() == "rel=\"next\"")
                .Select(p => p[0].Trim().TrimStart('<').TrimEnd('>'))
                .FirstOrDefault()
            : null;

    private static IReadOnlyList<string> Usernames(IReadOnlyList<UserDto>? users) => [.. (users ?? []).Select(u => u.Username)];

    // The project's full path, escaped into one segment: groups can nest, so owner may hold slashes.
    private static string ProjectPath(RepoRef repo) => $"projects/{Uri.EscapeDataString($"{repo.Owner}/{repo.Name}")}";

    private static string MergeRequestPath(PullRequestRef pr) =>
        $"{ProjectPath(pr.Repo)}/merge_requests/{pr.Number.ToString(CultureInfo.InvariantCulture)}";
}
