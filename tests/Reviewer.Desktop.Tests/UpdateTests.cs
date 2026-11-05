using System.Xml.Linq;
using Reviewer.Desktop.Services;
using Reviewer.Desktop.ViewModels;

namespace Reviewer.Desktop.Tests;

/// <summary>Updates, which must never replace the files under a running engine, and start at login.</summary>
public sealed class UpdateTests : IDisposable
{
    private readonly FakeEngine _engine = new();
    private readonly FakeUpdater _updater = new();
    private readonly FakeLoginItem _loginItem = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("reviewer-desktop-");

    public void Dispose() => _dir.Delete(recursive: true);

    private MainWindowViewModel Window(FakeLauncher launcher, ILoginItem? loginItem = null) => new(
        _engine,
        new EngineConnection(_engine, launcher, TimeSpan.FromSeconds(2)),
        new DesktopSettings(Path.Combine(_dir.FullName, DesktopSettings.FileName)),
        _updater,
        loginItem ?? _loginItem,
        action => action(),
        _ => Task.CompletedTask,
        _ => Task.CompletedTask);

    /// <summary>An app whose engine is up, started by the app itself or found running, with an update downloaded.</summary>
    private async Task<MainWindowViewModel> ReadyAsync(FakeLauncher launcher, bool startedHere)
    {
        _engine.Status = startedHere ? null : FakeEngine.Running;
        _updater.Latest = () => "1.1.0";
        var window = Window(launcher);
        await window.Engine.ConnectAsync(start: true, CancellationToken.None);
        await window.Updates.CheckAsync(CancellationToken.None);
        Assert.True(window.Updates.IsReady);
        return window;
    }

    [Fact]
    public async Task Check_FindsAndDownloadsANewerVersion()
    {
        using var window = Window(new FakeLauncher(_engine));
        Assert.Equal("Reviewer 1.0.0", window.Updates.Version);

        await window.Updates.CheckAsync(CancellationToken.None);
        Assert.False(window.Updates.IsReady);

        _updater.Latest = () => "1.1.0";
        await window.Updates.CheckAsync(CancellationToken.None);
        Assert.True(window.Updates.IsReady);
        Assert.Equal("Reviewer 1.1.0 is ready", window.Updates.ReadyText);
    }

    [Fact]
    public async Task Check_ThatFails_SaysWhy_AndTheNextMayWork()
    {
        using var window = Window(new FakeLauncher(_engine));
        _updater.Latest = () => throw new HttpRequestException("offline");

        await window.Updates.CheckAsync(CancellationToken.None);
        Assert.Equal("Couldn't check for updates: offline", window.Updates.Problem);
        Assert.False(window.Updates.IsReady);

        _updater.Latest = () => "1.1.0";
        await window.Updates.CheckAsync(CancellationToken.None);
        Assert.Null(window.Updates.Problem);
        Assert.True(window.Updates.IsReady);
    }

    [Fact]
    public async Task DevelopmentBuild_IsNeverUpdated_NorStartedAtLogin()
    {
        _updater.IsInstalled = false;
        _updater.CurrentVersion = null;
        using var window = Window(new FakeLauncher(_engine));

        await window.Updates.RunAsync(CancellationToken.None);

        Assert.Empty(_updater.Calls);
        Assert.Equal("Reviewer (development build)", window.Updates.Version);
        Assert.False(window.CanStartAtLogin);
    }

    [Fact]
    public async Task Run_StopsChecking_OnceAnUpdateIsReady()
    {
        _updater.Latest = () => "1.1.0";
        using var window = Window(new FakeLauncher(_engine));

        await window.Updates.RunAsync(CancellationToken.None);

        Assert.Equal(["check"], _updater.Calls);
    }

    [Fact]
    public async Task Quitting_AppliesTheUpdate_AfterStoppingTheEngine()
    {
        var launcher = new FakeLauncher(_engine);
        using var window = await ReadyAsync(launcher, startedHere: true);

        await window.ShutdownAsync();

        Assert.Equal(["shutdown"], _engine.Calls);
        Assert.Equal(["check", "apply-on-exit"], _updater.Calls);
    }

    [Fact]
    public async Task Quitting_WithTheEngineKeptRunning_LeavesTheUpdateForLater()
    {
        var launcher = new FakeLauncher(_engine);
        using var window = await ReadyAsync(launcher, startedHere: true);
        window.KeepEngineRunning = true;

        await window.ShutdownAsync();

        Assert.Empty(_engine.Calls);
        Assert.Equal(["check"], _updater.Calls);
    }

    [Fact]
    public async Task Quitting_WhileAnEarlierRunsEngineUsesTheAppsFolder_LeavesTheUpdateForLater()
    {
        var launcher = new FakeLauncher(_engine) { Bundled = new FakeProcess() };
        using var window = await ReadyAsync(launcher, startedHere: false);

        await window.ShutdownAsync();

        Assert.Empty(_engine.Calls);
        Assert.Equal(["check"], _updater.Calls);
    }

    [Fact]
    public async Task Quitting_WithAnEngineFromElsewhere_AppliesTheUpdate_AndLeavesTheEngine()
    {
        var launcher = new FakeLauncher(_engine);
        using var window = await ReadyAsync(launcher, startedHere: false);

        await window.ShutdownAsync();

        Assert.Empty(_engine.Calls);
        Assert.Equal(["check", "apply-on-exit"], _updater.Calls);
    }

    [Fact]
    public async Task RestartToUpdate_StopsTheEngineTheAppStarted_EvenWhenKeptRunning()
    {
        var launcher = new FakeLauncher(_engine);
        using var window = await ReadyAsync(launcher, startedHere: true);
        window.KeepEngineRunning = true;

        await window.RestartToUpdateCommand.ExecuteAsync(null);

        Assert.Equal(["shutdown"], _engine.Calls);
        Assert.True(launcher.Process!.HasExited);
        Assert.Equal(["check", "restart"], _updater.Calls);
    }

    [Fact]
    public async Task RestartToUpdate_StopsAnEngineRunningFromTheAppsFolder()
    {
        var bundled = new FakeProcess();
        using var window = await ReadyAsync(new FakeLauncher(_engine) { Bundled = bundled }, startedHere: false);

        await window.RestartToUpdateCommand.ExecuteAsync(null);

        Assert.Equal(["shutdown"], _engine.Calls);
        Assert.True(bundled.HasExited);
        Assert.Equal(["check", "restart"], _updater.Calls);
    }

    [Fact]
    public async Task RestartToUpdate_LeavesAnEngineFromElsewhere()
    {
        using var window = await ReadyAsync(new FakeLauncher(_engine), startedHere: false);

        await window.RestartToUpdateCommand.ExecuteAsync(null);

        Assert.Empty(_engine.Calls);
        Assert.Equal(["check", "restart"], _updater.Calls);
    }

    [Fact]
    public void StartAtLogin_ShowsAndSetsTheLoginItem()
    {
        _loginItem.IsEnabled = true;
        using var window = Window(new FakeLauncher(_engine));
        Assert.True(window.CanStartAtLogin);
        Assert.True(window.StartAtLogin);

        window.StartAtLogin = false;

        Assert.False(_loginItem.IsEnabled);
        Assert.Null(window.PreferencesProblem);
    }

    [Fact]
    public void StartAtLogin_ThatCantBeSet_SaysWhy_AndShowsWhatIsSet()
    {
        _loginItem.Refuse = "Couldn't add the login item: denied";
        using var window = Window(new FakeLauncher(_engine));

        window.StartAtLogin = true;

        Assert.False(window.StartAtLogin);
        Assert.Equal("Couldn't add the login item: denied", window.PreferencesProblem);
    }

    [Fact]
    public void StartAtLogin_IsntOffered_WithoutALoginItemForTheOs()
    {
        using var window = new MainWindowViewModel(
            _engine,
            new EngineConnection(_engine, new FakeLauncher(_engine)),
            new DesktopSettings(Path.Combine(_dir.FullName, DesktopSettings.FileName)),
            _updater,
            loginItem: null,
            action => action(),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);

        Assert.False(window.CanStartAtLogin);
        window.StartAtLogin = true;
        Assert.Null(window.PreferencesProblem);
    }

    [Fact]
    public void LaunchAgent_InABundle_OpensTheAppInTheBackground()
    {
        var plist = Path.Combine(_dir.FullName, "LaunchAgents", LoginItem.BundleId + ".plist");
        var item = new LaunchAgentLoginItem(plist, "/Applications/R & D/Reviewer.app/Contents/MacOS/Reviewer.Desktop");
        Assert.False(item.IsEnabled);

        Assert.Null(item.Set(true));

        Assert.True(item.IsEnabled);
        var dict = Load(plist);
        Assert.Equal(LoginItem.BundleId, Value(dict, "Label"));
        Assert.Equal(
            ["/usr/bin/open", "-a", "/Applications/R & D/Reviewer.app", "--args", "--background"],
            Arguments(dict));
        Assert.Equal("true", ((XElement)Key(dict, "RunAtLoad").NextNode!).Name.LocalName);
        if (OperatingSystem.IsMacOS())
        {
            // launchd's own reader agrees.
            using var lint = System.Diagnostics.Process.Start("/usr/bin/plutil", ["-lint", plist])!;
            lint.WaitForExit();
            Assert.Equal(0, lint.ExitCode);
        }

        Assert.Null(item.Set(false));
        Assert.False(item.IsEnabled);
        Assert.Null(item.Set(false));
    }

    [Fact]
    public void LaunchAgent_OutsideABundle_RunsTheExecutable()
    {
        var plist = Path.Combine(_dir.FullName, LoginItem.BundleId + ".plist");
        var item = new LaunchAgentLoginItem(plist, "/opt/reviewer/Reviewer.Desktop");

        Assert.Null(item.Set(true));

        Assert.Equal(["/opt/reviewer/Reviewer.Desktop", "--background"], Arguments(Load(plist)));
    }

    [Theory]
    [InlineData("/Applications/Reviewer.app/Contents/MacOS/Reviewer.Desktop", "/Applications/Reviewer.app")]
    [InlineData("/Applications/Reviewer.app/Contents/Resources/Reviewer.Desktop", null)]
    [InlineData("/opt/Contents/MacOS/Reviewer.Desktop", null)]
    [InlineData("Reviewer.Desktop", null)]
    public void LaunchAgent_FindsTheBundle(string executable, string? bundle) =>
        Assert.Equal(bundle, LaunchAgentLoginItem.Bundle(executable));

    [Fact]
    public void LaunchAgent_ThatCantBeWritten_SaysWhy()
    {
        var plist = Path.Combine(_dir.FullName, "agent.plist");
        Directory.CreateDirectory(plist + ".tmp");

        Assert.StartsWith("Couldn't add the login item", new LaunchAgentLoginItem(plist, "/opt/r").Set(true), StringComparison.Ordinal);
    }

    [Fact]
    public void RunKey_AddsAndRemovesTheEntry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var item = new RunKeyLoginItem($"Reviewer test {Guid.NewGuid():N}", @"C:\Users\me\AppData\Local\Reviewer\Reviewer.Desktop.exe");
        try
        {
            Assert.False(item.IsEnabled);
            Assert.Null(item.Set(true));
            Assert.True(item.IsEnabled);
            Assert.Equal(@"""C:\Users\me\AppData\Local\Reviewer\Reviewer.Desktop.exe"" --background", item.Command);
        }
        finally
        {
            Assert.Null(item.Set(false));
        }

        Assert.False(item.IsEnabled);
    }

    private static XElement Load(string plist) =>
        XDocument.Parse(File.ReadAllText(plist), LoadOptions.None).Root!.Element("dict")!;

    private static XElement Key(XElement dict, string key) => dict.Elements("key").Single(k => k.Value == key);

    private static string Value(XElement dict, string key) => ((XElement)Key(dict, key).NextNode!).Value;

    private static string[] Arguments(XElement dict) =>
        [.. ((XElement)Key(dict, "ProgramArguments").NextNode!).Elements("string").Select(s => s.Value)];
}
