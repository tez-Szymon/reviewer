namespace Reviewer.Providers.GitHub;

// Only the fields the adapter reads. Names map via snake_case.
internal sealed record UserDto(string Login);

internal sealed record LabelDto(string Name);

internal sealed record BranchDto(string Sha, string Ref);

internal sealed record PullRequestDto(
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
