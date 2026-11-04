using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Reviewer.Api;
using Reviewer.Desktop.Services;
using Reviewer.Engine.State;

namespace Reviewer.Desktop.Tests;

/// <summary>An engine client that answers from lists the test fills, with events the test sends.</summary>
internal sealed class FakeEngine : IEngineClient
{
    public static readonly EngineStatus Running = new("test", 42, DateTimeOffset.UnixEpoch, "/data");

    /// <summary>Null while no engine is listening.</summary>
    public EngineStatus? Status { get; set; } = Running;

    public List<JobDto> Queue { get; } = [];

    public List<JobSummaryDto> Jobs { get; } = [];

    public List<WatchedPullRequestDto> Pulls { get; } = [];

    public List<AccountDto> Accounts { get; } = [new("github", "github", new Uri("https://github.com"), null)];

    public Dictionary<string, JobDetailsDto> Details { get; } = [];

    /// <summary>Called while a list loads, as when events arrive mid-load.</summary>
    public Action? DuringLoad { get; set; }

    public List<string> Calls { get; } = [];

    public List<long?> WatchedAfter { get; } = [];

    public Channel<JobDto> Events { get; private set; } = Channel.CreateUnbounded<JobDto>();

    /// <summary>Ends the current stream as a stopped engine would; the next watch gets a new one.</summary>
    public void EndStream()
    {
        Events.Writer.Complete();
    }

    public Task<EngineStatus?> GetStatusAsync(CancellationToken ct) => Task.FromResult(Status);

    public Task<List<AgentStatus>> GetAgentsAsync(CancellationToken ct) => Task.FromResult(new List<AgentStatus>());

    public Task<List<AccountDto>> GetAccountsAsync(CancellationToken ct) => Task.FromResult(Accounts);

    public Task<List<JobDto>> GetQueueAsync(CancellationToken ct) => Load(Queue);

    public Task<List<JobSummaryDto>> ListJobsAsync(string? repo, int? number, int limit, CancellationToken ct) => Load(Jobs);

    public Task<JobDetailsDto?> GetJobAsync(string id, CancellationToken ct) => Task.FromResult(Details.GetValueOrDefault(id));

    public Task<CancelResult> CancelJobAsync(string id, CancellationToken ct)
    {
        Calls.Add($"cancel {id}");
        return id == "gone"
            ? Task.FromException<CancelResult>(new EngineApiException(HttpStatusCode.Conflict, "gone is succeeded; there's nothing to cancel."))
            : Task.FromResult(new CancelResult(id, JobState.Cancelled));
    }

    public Task<JobDto> RetryJobAsync(string id, CancellationToken ct)
    {
        Calls.Add($"retry {id}");
        return Task.FromResult(Queue.Single(j => j.Id == id));
    }

    public Task<QueueResult> QueueReviewAsync(QueueRequest request, CancellationToken ct) => throw new NotSupportedException();

    public Task<List<WatchedPullRequestDto>> ListPullsAsync(string? repo, CancellationToken ct) => Load(Pulls);

    public Task<PullRequestDto> GetPullAsync(string? host, string repo, int number, CancellationToken ct) => throw new NotSupportedException();

    public Task ShutdownAsync(CancellationToken ct)
    {
        Calls.Add("shutdown");
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<JobDto> WatchJobsAsync(long? after, Action? opened, [EnumeratorCancellation] CancellationToken ct)
    {
        WatchedAfter.Add(after);
        if (Status is null)
        {
            throw new HttpRequestException("No engine.", new IOException());
        }

        var events = Events;
        opened?.Invoke();
        await foreach (var job in events.Reader.ReadAllAsync(ct))
        {
            yield return job;
        }

        Events = Channel.CreateUnbounded<JobDto>();
    }

    private Task<List<T>> Load<T>(List<T> list)
    {
        var copy = list.ToList();
        DuringLoad?.Invoke();
        return Task.FromResult(copy);
    }

    public static JobDto Job(string id, JobState state, long version, int number = 7, string repo = "acme/api", DateTimeOffset? created = null) => new(
        Id: id, Host: "https://github.com", Repo: repo, Number: number, State: state, AgentId: "claude-code", DryRun: false, Full: false,
        Published: state == JobState.Succeeded, Decision: null, Merge: null, HeadSha: null, BaseSha: null, Delta: null, DeltaFrom: null,
        Summary: null, Error: null, CreatedAt: created ?? DateTimeOffset.UnixEpoch.AddMinutes(version), StartedAt: null, FinishedAt: null,
        RetryAt: null, NotBefore: null, CancelRequested: false, RunnerPid: null, AttemptFolder: null, Version: version);
}

/// <summary>A launcher whose engine starts by making the fake engine answer, or never does.</summary>
internal sealed class FakeLauncher(FakeEngine engine) : IEngineLauncher
{
    public string LogPath => "/data/engine.log";

    public int Starts { get; private set; }

    public bool Answers { get; set; } = true;

    public bool ExitsAtOnce { get; set; }

    public string? Refuse { get; set; }

    public FakeProcess? Process { get; private set; }

    public IEngineProcess Start()
    {
        if (Refuse is { } message)
        {
            throw new EngineLaunchException(message);
        }

        Starts++;
        if (Answers)
        {
            engine.Status = FakeEngine.Running;
        }

        return Process = new FakeProcess { HasExited = ExitsAtOnce };
    }
}

internal sealed class FakeProcess : IEngineProcess
{
    public bool HasExited { get; set; }

    public bool Killed { get; private set; }

    public Task WaitForExitAsync(CancellationToken ct)
    {
        HasExited = true;
        return Task.CompletedTask;
    }

    public void Kill() => (Killed, HasExited) = (true, true);

    public void Dispose()
    {
    }
}
