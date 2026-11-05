using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Reviewer.Api;
using Reviewer.Core.Findings;
using Reviewer.Core.Policy;
using Reviewer.Core.Providers;
using Reviewer.Desktop.Services;
using Reviewer.Desktop.ViewModels;
using Reviewer.Desktop.Views;
using Reviewer.Engine;
using Reviewer.Engine.State;

namespace Reviewer.Desktop.Tests;

/// <summary>The app as Avalonia's headless platform draws it, with Skia, so XAML and bindings are exercised for real.</summary>
internal static class HeadlessApp
{
    public static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseInter();
}

/// <summary>
/// Every screen, light and dark, with jobs, PRs, findings and settings in it. Set <c>REVIEWER_SCREENSHOTS</c> to a
/// folder to save each frame there as a PNG, to look at the design without running the app.
/// </summary>
public sealed class ScreenTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    [Fact]
    public Task EveryScreen_Renders_InLightAndDark() => HeadlessApp.Session.Dispatch(async () =>
    {
        var engine = Sample();
        using var model = new MainWindowViewModel(
            engine,
            new EngineConnection(engine, new FakeLauncher(engine)),
            new DesktopSettings(Path.Combine(Path.GetTempPath(), $"reviewer-screens-{Guid.NewGuid():N}.json")),
            new FakeUpdater { Latest = () => "1.4.0" },
            new FakeLoginItem(),
            action => action(),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
        await model.Engine.ConnectAsync(start: false, CancellationToken.None);
        await Task.WhenAll(model.Pages.Select(p => p.LoadAsync()));
        await model.Updates.CheckAsync(CancellationToken.None);

        var window = new MainWindow { DataContext = model, Width = 1280, Height = 820 };
        window.Show();
        var folder = Environment.GetEnvironmentVariable("REVIEWER_SCREENSHOTS");

        foreach (var (variant, name) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            foreach (var page in model.Pages)
            {
                model.SelectedPage = page;
                if (page is ReposViewModel repos)
                {
                    repos.Selected = repos.Watched.FirstOrDefault();
                }

                Capture(window, folder, $"{page.GetType().Name.Replace("ViewModel", "", StringComparison.Ordinal)}-{name}");
            }

            model.OpenJob("j-done");
            await ((JobDetailViewModel)model.Current).LoadAsync();
            Capture(window, folder, $"Review-{name}");
        }

        // No engine: the banner, and empty pages.
        var empty = new FakeEngine { Status = null };
        using var offline = new MainWindowViewModel(
            empty,
            new EngineConnection(empty, new FakeLauncher(empty) { Answers = false }, TimeSpan.FromMilliseconds(10)),
            new DesktopSettings(Path.Combine(Path.GetTempPath(), $"reviewer-screens-{Guid.NewGuid():N}.json")),
            new FakeUpdater(),
            null,
            action => action(),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
        await offline.Engine.ConnectAsync(start: false, CancellationToken.None);
        window.DataContext = offline;
        foreach (var (variant, name) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            foreach (var page in offline.Pages.Take(3))
            {
                offline.SelectedPage = page;
                Capture(window, folder, $"Offline-{page.GetType().Name.Replace("ViewModel", "", StringComparison.Ordinal)}-{name}");
            }
        }

        window.Close();
    }, CancellationToken.None);

    private static void Capture(Window window, string? folder, string name)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
            frame.Save(Path.Combine(folder, $"{name}.png"), new PngBitmapEncoderOptions());
        }
    }

    private static FakeEngine Sample()
    {
        var engine = new FakeEngine();
        engine.Queue.AddRange(
        [
            Job("j-run", JobState.Running, 10, 412, "acme/payments", Now.AddMinutes(-3)) with { StartedAt = Now.AddMinutes(-2) },
            Job("j-wait", JobState.Queued, 11, 87, "acme/web", Now.AddMinutes(-1)),
            Job("j-limit", JobState.Throttled, 12, 1290, "acme/api", Now.AddMinutes(-20)) with
            {
                RetryAt = Now.AddMinutes(40), Error = "Claude usage limit reached; resets at the top of the hour.",
            },
        ]);
        engine.Jobs.AddRange(
        [
            new(Done("j-done", 412, "acme/payments", ReviewDecision.RequestChanges), 3, 1, 1),
            new(Done("j-ok", 88, "acme/web", ReviewDecision.Approve) with { Merge = MergeIntent.MergeWhenGreen }, 0, 1, 0),
            new(Done("j-note", 1287, "acme/api", null), 2, 0, 0),
            new(Job("j-fail", JobState.Failed, 4, 1281, "acme/api", Now.AddHours(-30)) with { Error = "The agent exited with code 1." }, 0, 0, 0),
            new(Job("j-skip", JobState.Skipped, 3, 86, "acme/web", Now.AddDays(-2)), 0, 0, 0),
        ]);
        engine.Details["j-done"] = new(engine.Jobs[0].Job, Findings(),
        [
            "1 blocking finding: changes requested.",
            "3 findings inline, 1 outside the delta listed in the summary.",
            "Not merged: changes were requested.",
        ]);
        engine.Pulls.AddRange(
        [
            Pull(412, "acme/payments", "Retry failed webhooks with exponential backoff", "mlopez", "c0ffee1", "c0ffee1", engine.Jobs[0]),
            Pull(88, "acme/web", "Dark mode for the settings page", "jkim", "d00d123", "abc1234", engine.Jobs[1]),
            Pull(1290, "acme/api", "Bump Npgsql to 9.0.3", "dependabot", "fe12ab9", null, null),
            Pull(77, "acme/web", "WIP: new onboarding flow", "tsato", "1234567", null, null, matched: false),
        ]);
        engine.Agents.AddRange(
        [
            new AgentStatus("claude-code", true, true, true, "2.3.1", null),
            new AgentStatus("codex", false, true, false, null, null),
            new AgentStatus("anthropic-api", true, true, true, "claude-opus-5-5", null),
        ]);
        engine.ConfigYaml = """
            accounts:
              - { id: gh, provider: github, token: "keychain:reviewer/gh" }
              - { id: gl, provider: gitlab, host: https://git.example.com, token: "env:GITLAB_TOKEN" }
            defaults: { review: { mode: approve } }
            repos:
              - repo: acme/payments
                account: gh
                overrides: { review: { mode: gate } }
              - repo: acme/web
                account: gh
                agent: codex
              - repo: platform/api
                account: gl

            """;
        engine.Accounts.Clear();
        engine.Accounts.AddRange(
        [
            new("gh", "github", new Uri("https://github.com"), "keychain:reviewer/gh"),
            new("gl", "gitlab", new Uri("https://git.example.com"), "env:GITLAB_TOKEN"),
        ]);
        engine.AccountStatuses["gl"] = new AccountStatus("gl", false, null, "GITLAB_TOKEN isn't set.");
        return engine;
    }

    private static JobDto Job(string id, JobState state, long version, int number, string repo, DateTimeOffset created) =>
        FakeEngine.Job(id, state, version, number, repo, created) with { HeadSha = "c0ffee1d2", BaseSha = "0badf00d" };

    private static JobDto Done(string id, int number, string repo, ReviewDecision? decision) =>
        Job(id, JobState.Succeeded, 5, number, repo, Now.AddHours(-1)) with
        {
            Decision = decision, Published = true, StartedAt = Now.AddMinutes(-58), FinishedAt = Now.AddMinutes(-55),
            Delta = DeltaKind.NewCommits, DeltaFrom = "9a8b7c6d5", AttemptFolder = "/data/jobs/j-done/1",
            Summary = "Adds exponential backoff to webhook retries. The retry loop can spin forever when the endpoint returns 410, and the new table lacks an index the scheduler queries by.",
        };

    private static WatchedPullRequestDto Pull(
        int number, string repo, string title, string author, string head, string? reviewed, JobSummaryDto? latest, bool matched = true) =>
        new("https://github.com", repo, number, title, author, head, matched, false, true, Now,
            reviewed is null ? null : new LastReviewDto(reviewed, "0badf00d", Now.AddHours(-1), latest?.Job.Id ?? "x"), latest);

    private static List<FindingDto> Findings() =>
    [
        new("f1", Severity.Blocker, Category.Correctness, 0.94, "src/Webhooks/RetryLoop.cs", 42, 57,
            "Retry loop never ends on 410 Gone",
            "A 410 means the endpoint was removed, but it's treated as transient, so the job retries forever and holds a worker.",
            "if (response.StatusCode == HttpStatusCode.Gone)\n{\n    return RetryDecision.GiveUp;\n}", FindingDisposition.Inline, true, "fp1", null),
        new("f2", Severity.Major, Category.Performance, 0.81, "src/Db/Migrations/0042_webhook_attempts.sql", 12, 12,
            "Missing index on webhook_attempts(next_attempt_at)",
            "The scheduler selects due attempts by next_attempt_at every second; without an index this scans the table.",
            null, FindingDisposition.Inline, false, "fp2", null),
        new("f3", Severity.Minor, Category.Tests, 0.72, "tests/Webhooks/RetryLoopTests.cs", 88, 90,
            "Backoff cap isn't tested", "Nothing checks that the delay stops growing at the configured maximum.",
            null, FindingDisposition.Summary, false, "fp3", null),
        new("f4", Severity.Nit, Category.Style, 0.66, "src/Webhooks/Backoff.cs", 5, 5,
            "Prefer TimeSpan.FromSeconds over a magic number", "30 reads better as TimeSpan.FromSeconds(30).",
            null, FindingDisposition.AlreadyPosted, false, "fp4", "j-later"),
    ];
}
