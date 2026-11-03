namespace Reviewer.Providers.GitLab;

// Only the fields the adapter reads. Names map via snake_case.
internal sealed record UserDto(long Id, string Username);

internal sealed record MergeRequestDto(
    int Iid,
    string? Title,
    string? Description,
    string Sha,
    string TargetBranch,
    UserDto Author,
    IReadOnlyList<UserDto>? Assignees,
    IReadOnlyList<UserDto>? Reviewers,
    IReadOnlyList<string>? Labels,
    bool Draft,
    DiffRefsDto? DiffRefs,
    PipelineDto? HeadPipeline,
    string? DetailedMergeStatus,
    string? MergeStatus);

/// <param name="BaseSha">The merge base.</param>
/// <param name="StartSha">The target branch's head when this version was made.</param>
internal sealed record DiffRefsDto(string BaseSha, string HeadSha, string StartSha);

internal sealed record PipelineDto(string Status);

internal sealed record DiffDto(string OldPath, string NewPath, string? Diff, bool DeletedFile);

internal sealed record VersionDto(long Id, string HeadCommitSha, string BaseCommitSha, string StartCommitSha);

internal sealed record VersionDetailsDto(long Id, string HeadCommitSha, string BaseCommitSha, string StartCommitSha, IReadOnlyList<DiffDto> Diffs);

internal sealed record DiscussionDto(string Id, IReadOnlyList<NoteDto> Notes);

internal sealed record NoteDto(long Id, string Body, UserDto? Author, PositionDto? Position, bool System, bool Resolvable, bool Resolved);

internal sealed record PositionDto(string? NewPath, string? OldPath, int? NewLine, int? OldLine);

internal sealed record ApprovalsDto(IReadOnlyList<ApprovedByDto>? ApprovedBy);

internal sealed record ApprovedByDto(UserDto User);

internal sealed record NoteRequest(string Body);

internal sealed record DiscussionRequest(string Body, PositionRequest Position);

internal sealed record PositionRequest(
    string PositionType,
    string BaseSha,
    string StartSha,
    string HeadSha,
    string OldPath,
    string NewPath,
    int NewLine,
    int? OldLine);

internal sealed record ApproveRequest(string Sha);

internal sealed record ResolveRequest(bool Resolved);

internal sealed record MergeRequestMerge(string Sha, bool Squash, bool AutoMerge, bool MergeWhenPipelineSucceeds);
