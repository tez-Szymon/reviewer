using Reviewer.Api;
using Reviewer.Desktop.Services;
using Reviewer.Engine.State;

namespace Reviewer.Desktop.Tests;

public class EngineTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private readonly FakeEngine _engine = new();

    private EngineConnection Connection(FakeLauncher launcher) => new(_engine, launcher, TimeSpan.FromSeconds(1));

    [Fact]
    public async Task Connect_UsesARunningEngine_WithoutStartingOne()
    {
        var launcher = new FakeLauncher(_engine);
        using var connection = Connection(launcher);

        await connection.ConnectAsync(start: true, Ct);

        Assert.Equal(EngineState.Connected, connection.State);
        Assert.Equal(0, launcher.Starts);
        Assert.False(connection.StartedHere);
    }

    [Fact]
    public async Task Connect_StartsTheEngine_AndStopsItAtTheEnd()
    {
        _engine.Status = null;
        var launcher = new FakeLauncher(_engine);
        using var connection = Connection(launcher);

        await connection.ConnectAsync(start: true, Ct);
        Assert.Equal(EngineState.Connected, connection.State);
        Assert.True(connection.StartedHere);

        await connection.StopAsync();
        Assert.Equal(["shutdown"], _engine.Calls);
        Assert.False(launcher.Process!.Killed);
    }

    [Fact]
    public async Task Connect_WithoutStarting_ReportsNoEngine()
    {
        _engine.Status = null;
        using var connection = Connection(new FakeLauncher(_engine));

        await connection.ConnectAsync(start: false, Ct);

        Assert.Equal(EngineState.Unavailable, connection.State);
        Assert.Equal("The engine isn't running.", connection.Description);
    }

    [Theory]
    [InlineData(false, false, null, "didn't answer within 1 seconds; see /data/engine.log")]
    [InlineData(false, true, null, "stopped right after starting; see /data/engine.log")]
    [InlineData(false, false, "No CLI.", "No CLI.")]
    public async Task Connect_SaysWhyTheEngineDidntStart(bool answers, bool exits, string? refuse, string problem)
    {
        _engine.Status = null;
        using var connection = Connection(new FakeLauncher(_engine) { Answers = answers, ExitsAtOnce = exits, Refuse = refuse });

        await connection.ConnectAsync(start: true, Ct);

        Assert.Equal(EngineState.Unavailable, connection.State);
        Assert.Contains(problem, connection.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_LeavesAnEngineStartedElsewhere()
    {
        using var connection = Connection(new FakeLauncher(_engine));
        await connection.ConnectAsync(start: true, Ct);

        await connection.StopAsync();

        Assert.Empty(_engine.Calls);
    }

    [Fact]
    public async Task Feed_OpensBeforeChanges_ReportsLoss_AndResumesAfterTheLastVersion()
    {
        var events = new List<string>();
        var feed = new JobFeed(_engine, action => action());
        feed.Opened += () => events.Add("opened");
        feed.Changed += job => events.Add($"{job.Id}@{job.Version}");
        feed.Lost += () => events.Add("lost");

        _engine.Events.Writer.TryWrite(FakeEngine.Job("a", JobState.Queued, 3));
        _engine.Events.Writer.TryWrite(FakeEngine.Job("a", JobState.Running, 4));
        _engine.EndStream();
        await feed.RunAsync(Ct);
        _engine.EndStream();
        await feed.RunAsync(Ct);

        Assert.Equal(["opened", "a@3", "a@4", "lost", "opened", "lost"], events);
        Assert.Equal([null, 4], _engine.WatchedAfter);
    }

    [Fact]
    public async Task Feed_WithoutAnEngine_IsLost()
    {
        _engine.Status = null;
        var lost = false;
        var feed = new JobFeed(_engine, action => action());
        feed.Lost += () => lost = true;

        await feed.RunAsync(Ct);

        Assert.True(lost);
    }
}
