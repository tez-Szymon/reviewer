namespace Reviewer.Providers.GitHub;

// Only the fields the adapter reads. Names map via snake_case.
internal sealed record UserDto(string Login);

internal sealed record LabelDto(string Name);

internal sealed record BranchDto(string Sha, string Ref);

internal sealed record PullRequestDto(
    string? Title,
    string? Body,
    BranchDto Head,
    BranchDto Base,
    UserDto User,
    IReadOnlyList<UserDto>? Assignees,
    IReadOnlyList<UserDto>? RequestedReviewers,
    IReadOnlyList<LabelDto>? Labels,
    bool Draft,
    bool? Mergeable);

internal sealed record CombinedStatusDto(string State, int TotalCount);

internal sealed record CheckRunDto(string Status, string? Conclusion);

internal sealed record CheckRunsDto(int TotalCount, IReadOnlyList<CheckRunDto> CheckRuns);

internal sealed record ReviewCommentDto(long Id, UserDto? User, string Body, string Path, int? Line);

internal sealed record ReviewCommentRequest(string Path, string Body, int Line, string Side, int? StartLine, string? StartSide);

internal sealed record ReviewRequest(string CommitId, string Body, string Event, IReadOnlyList<ReviewCommentRequest> Comments);

internal sealed record MergeRequest(string MergeMethod, string Sha);

internal sealed record ErrorDto(string? Message);

internal sealed record ReplyRequest(string Body);

// GraphQL, camelCase. Review threads are GraphQL-only: REST has no thread ids or resolved state.
internal sealed record GraphQlRequest(string Query, object Variables);

internal sealed record GraphQlResponse<T>(T? Data, IReadOnlyList<GraphQlError>? Errors);

internal sealed record GraphQlError(string Message);

internal sealed record ThreadsData(ThreadsRepository? Repository);

internal sealed record ThreadsRepository(ThreadsPullRequest? PullRequest);

internal sealed record ThreadsPullRequest(ThreadConnection ReviewThreads);

internal sealed record ThreadConnection(PageInfo PageInfo, IReadOnlyList<ThreadNode> Nodes);

internal sealed record PageInfo(bool HasNextPage, string? EndCursor);

internal sealed record ThreadNode(string Id, bool IsResolved, ThreadComments Comments);

internal sealed record ThreadComments(IReadOnlyList<ThreadComment> Nodes);

internal sealed record ThreadComment(long? DatabaseId);

internal sealed record ResolveData(ResolvePayload? ResolveReviewThread);

internal sealed record ResolvePayload(ThreadState? Thread);

internal sealed record ThreadState(bool IsResolved);
