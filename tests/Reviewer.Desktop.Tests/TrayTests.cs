using Reviewer.Desktop.Services;
using Reviewer.Desktop.ViewModels;
using Reviewer.Engine.State;

namespace Reviewer.Desktop.Tests;

/// <summary>What the tray needs from the window's model: what the engine is doing, and whether quitting stops it.</summary>
public sealed class TrayTests : IDisposable
{
    private readonly FakeEngine _engine = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("reviewer-desktop-");

    private string SettingsPath => Path.Combine(_dir.FullName, DesktopSettings.FileName);

    public void Dispose() => _dir.Delete(recursive: true);

    private MainWindowViewModel Window(FakeLauncher launcher) => new(
        _engine,
        new EngineConnection(_engine, launcher, TimeSpan.FromSeconds(2)),
        new DesktopSettings(SettingsPath),
        action => action(),
        _ => Task.CompletedTask,
        _ => Task.CompletedTask);

    [Fact]
    public async Task Quitting_StopsAnEngineTheAppStarted()
    {
        _engine.Status = null;
        var launcher = new FakeLauncher(_engine);
        using var window = Window(launcher);
        await window.Engine.ConnectAsync(start: true, CancellationToken.None);

        await window.ShutdownAsync();

        Assert.Equal(["shutdown"], _engine.Calls);
    }

    [Fact]
    public async Task Quitting_WithKeepRunning_LeavesTheEngineUp()
    {
        _engine.Status = null;
        var launcher = new FakeLauncher(_engine);
        using var window = Window(launcher);
        await window.Engine.ConnectAsync(start: true, CancellationToken.None);
        window.KeepEngineRunning = true;

        await window.ShutdownAsync();

        Assert.Empty(_engine.Calls);
        Assert.False(launcher.Process!.HasExited);
    }

    [Fact]
    public void KeepRunning_IsRemembered()
    {
        using (var first = Window(new FakeLauncher(_engine)))
        {
            Assert.False(first.KeepEngineRunning);
            first.KeepEngineRunning = true;
            Assert.Null(first.PreferencesProblem);
        }

        using var second = Window(new FakeLauncher(_engine));
        Assert.True(second.KeepEngineRunning);
    }

    [Fact]
    public void Preferences_ThatCantBeRead_AreTheDefaults()
    {
        File.WriteAllText(SettingsPath, "{ not json");

        Assert.Equal(new DesktopPreferences(), new DesktopSettings(SettingsPath).Load());
        Assert.Equal(new DesktopPreferences(), new DesktopSettings(Path.Combine(_dir.FullName, "missing", DesktopSettings.FileName)).Load());
    }

    [Fact]
    public void Preferences_ThatCantBeSaved_SayWhy()
    {
        // A directory where the file should be.
        Directory.CreateDirectory(SettingsPath);

        using var window = Window(new FakeLauncher(_engine));
        window.KeepEngineRunning = true;

        Assert.StartsWith("Couldn't save desktop.json", window.PreferencesProblem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrayText_SaysWhatTheQueueHolds_OrWhyThereIsNoEngine()
    {
        _engine.Status = null;
        using var window = Window(new FakeLauncher(_engine));
        var changes = new List<string?>();
        window.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        await window.Engine.ConnectAsync(start: false, CancellationToken.None);
        Assert.Equal("Reviewer: The engine isn't running.", window.TrayText);

        _engine.Status = FakeEngine.Running;
        await window.Engine.ConnectAsync(start: false, CancellationToken.None);
        Assert.Equal("Reviewer: idle", window.TrayText);

        window.Queue.OnChanged(FakeEngine.Job("a", JobState.Running, 1));
        window.Queue.OnChanged(FakeEngine.Job("b", JobState.Queued, 2));
        window.Queue.OnChanged(FakeEngine.Job("c", JobState.Queued, 3));
        window.Queue.OnChanged(FakeEngine.Job("d", JobState.Throttled, 4));
        Assert.Equal("Reviewer: 1 reviewing, 2 queued, 1 throttled", window.TrayText);

        window.Queue.OnChanged(FakeEngine.Job("a", JobState.Succeeded, 5));
        Assert.Equal("Reviewer: 2 queued, 1 throttled", window.TrayText);
        Assert.Contains(nameof(MainWindowViewModel.TrayText), changes);
    }
}
