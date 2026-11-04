using Microsoft.Extensions.AI;
using Reviewer.Git;

namespace Reviewer.Agents.Api.Tests;

/// <summary>A chat client that answers from a script, and records what each request carried.</summary>
internal sealed class ScriptedChat(params ChatResponse[] script) : IChatClient
{
    private readonly Queue<ChatResponse> _script = new(script);

    /// <summary>Each request's messages, copied as they were sent, and its options.</summary>
    public List<(List<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.Add(([.. messages], options?.Clone()));
        return _script.Count > 0
            ? Task.FromResult(_script.Dequeue())
            : throw new InvalidOperationException("The script ran out.");
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    public static ChatResponse Text(string text, ChatFinishReason? finish = null) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = finish ?? ChatFinishReason.Stop };

    public static ChatResponse Calls(params (string Name, Dictionary<string, object?> Arguments)[] calls) =>
        new(new ChatMessage(ChatRole.Assistant, [.. calls.Select((c, i) => (AIContent)new FunctionCallContent($"call{i}", c.Name, c.Arguments))]))
        {
            FinishReason = ChatFinishReason.ToolCalls,
        };
}

/// <summary>A repo that has only the files a test gives it, at the commits it names.</summary>
internal sealed class FakeGit(Dictionary<string, string> files) : IGitRepository
{
    public List<string> Reads { get; } = [];

    public Task<string?> ReadFileAsync(string sha, string path, CancellationToken ct)
    {
        Reads.Add($"{sha}:{path}");
        return Task.FromResult(files.GetValueOrDefault($"{sha}:{path}"));
    }

    public Task FetchAsync(string remoteUrl, IReadOnlyList<string> refspecs, GitCredential? credential, CancellationToken ct) => throw new NotSupportedException();

    public Task AddWorktreeAsync(string sha, string path, CancellationToken ct) => throw new NotSupportedException();

    public Task RemoveWorktreeAsync(string path, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> MergeBaseAsync(string sha1, string sha2, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> DiffAsync(string fromSha, string toSha, CancellationToken ct, IReadOnlyCollection<string>? paths = null) => throw new NotSupportedException();

    public Task<IReadOnlyList<ChangedFile>> ChangedFilesAsync(string fromSha, string toSha, CancellationToken ct, IReadOnlyCollection<string>? paths = null) =>
        throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, IReadOnlyList<LineSpan>>> ChangedOldLinesAsync(string fromSha, string toSha, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<bool> HasCommitAsync(string sha, CancellationToken ct) => throw new NotSupportedException();

    public Task UpdateRefAsync(string name, string sha, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> CommitsAsync(string range, CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> IsAncestorAsync(string ancestorSha, string descendantSha, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, string>> PatchIdsAsync(string range, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>A worktree and an attempt folder with the inputs a review reads, deleted afterwards.</summary>
internal sealed class Workspace : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("reviewer-api-");

    public Workspace()
    {
        Directory.CreateDirectory(Worktree);
        Directory.CreateDirectory(Attempt);
        File.WriteAllText(Path.Combine(Attempt, "instructions.md"), "Look for bugs.");
        File.WriteAllText(Path.Combine(Attempt, "context.json"), """{ "title": "Fix totals" }""");
        File.WriteAllText(Path.Combine(Attempt, "delta.patch"), "--- a/src/a.cs\n+++ b/src/a.cs\n@@ -1 +1 @@\n-old\n+new\n");
        File.WriteAllText(Path.Combine(Attempt, "schema.json"), Reviewer.Core.Findings.FindingsSchema.Json);
    }

    public string Root => _dir.FullName;

    public string Worktree => Path.Combine(_dir.FullName, "worktree");

    public string Attempt => Path.Combine(_dir.FullName, "attempt");

    public string Write(string relative, string text)
    {
        var path = Path.Combine(Worktree, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => _dir.Delete(recursive: true);
}
