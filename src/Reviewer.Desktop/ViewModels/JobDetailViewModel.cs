using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reviewer.Api;
using Reviewer.Core.Findings;
using Reviewer.Desktop.Services;
using Reviewer.Engine;
using Reviewer.Engine.Config;
using Reviewer.Engine.State;

namespace Reviewer.Desktop.ViewModels;

/// <summary>A finding as the review detail shows it.</summary>
internal sealed class FindingItem(FindingDto finding)
{
    public FindingDto Finding { get; } = finding;

    public string Heading => $"{Finding.Severity.ToString().ToLowerInvariant()} · {Finding.Category.ToString().ToLowerInvariant()} · {Finding.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}";

    public string SeverityText => Finding.Severity.ToString();

    /// <summary>Blockers red, major amber, minor blue, nits grey.</summary>
    public Tone SeverityTone => Finding.Severity switch
    {
        Severity.Blocker => Tone.Danger,
        Severity.Major => Tone.Warning,
        Severity.Minor => Tone.Accent,
        _ => Tone.Neutral,
    };

    public string CategoryText => Finding.Category.ToString().ToLowerInvariant();

    public string ConfidenceText => $"{Math.Round(Finding.Confidence * 100).ToString(CultureInfo.InvariantCulture)}% confidence";

    public string Location => Finding.StartLine == Finding.EndLine
        ? $"{Finding.File}:{Finding.StartLine.ToString(CultureInfo.InvariantCulture)}"
        : $"{Finding.File}:{Finding.StartLine.ToString(CultureInfo.InvariantCulture)}-{Finding.EndLine.ToString(CultureInfo.InvariantCulture)}";

    public string Title => Finding.Title;

    public string Body => Finding.Body;

    /// <summary>What the policy did with it: posted inline, listed in the summary, already on the PR, or dropped.</summary>
    public string Disposition => Finding.Disposition switch
    {
        FindingDisposition.Inline => "inline comment",
        FindingDisposition.Summary => "in the summary",
        FindingDisposition.AlreadyPosted => "already on the PR",
        _ => "dropped",
    };

    public bool Blocking => Finding.Blocking;

    public string? Suggestion => Finding.Suggestion;

    /// <summary>A later review found it fixed, or a person resolved its thread.</summary>
    public bool Resolved => Finding.ResolvedByJobId is not null;
}

/// <summary>One job: its findings, the delta it covered, the policy's decision and why, and links out.</summary>
internal sealed partial class JobDetailViewModel(IEngineClient client, IShell shell, string jobId) : PageViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(StateText), nameof(Tone), nameof(DeltaText), nameof(HasAttemptFolder))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    private JobDto? _job;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenPullRequestCommand))]
    private Uri? _pullRequestUrl;

    [ObservableProperty]
    private IReadOnlyList<FindingItem> _findings = [];

    [ObservableProperty]
    private IReadOnlyList<string> _reasons = [];

    public string JobId { get; } = jobId;

    public override string Title => "Review";

    public string Heading => Job is { } job ? $"{Describe.Pr(job.Repo, job.Number)} · {job.AgentId}" : JobId;

    public string StateText => Job is { } job ? Describe.State(job) : "";

    public Tone Tone => Job is { } job ? Describe.Tone(job) : Tone.Neutral;

    /// <summary>What part of the PR was reviewed.</summary>
    public string DeltaText => Job switch
    {
        { Delta: null } => "",
        { Delta: DeltaKind.Full } job => $"Whole PR, {Describe.Short(job.DeltaFrom)}..{Describe.Short(job.HeadSha)}",
        { } job => $"What's new since {Describe.Short(job.DeltaFrom)}, up to {Describe.Short(job.HeadSha)}",
        null => "",
    };

    public bool HasAttemptFolder => Job?.AttemptFolder is not null;

    public override async Task LoadAsync()
    {
        Loading = true;
        await TryAsync(async () =>
        {
            var details = await client.GetJobAsync(JobId, CancellationToken.None)
                ?? throw new EngineApiException(System.Net.HttpStatusCode.NotFound, $"There's no job {JobId}.");
            Job = details.Job;
            Findings = [.. details.Findings.Select(f => new FindingItem(f))];
            Reasons = details.Reasons;
            PullRequestUrl ??= Link(details.Job, await client.GetAccountsAsync(CancellationToken.None));
        });
        Loading = false;
    }

    public override void OnChanged(JobDto job)
    {
        if (job.Id == Job?.Id && job.Version > Job.Version)
        {
            _ = LoadAsync();
        }
    }

    /// <summary>
    /// The PR's page, from the account for the job's host: a GitLab instance may be served from a path, which only its
    /// account knows. Null when no account matches.
    /// </summary>
    internal static Uri? Link(JobDto job, IReadOnlyList<AccountDto> accounts)
    {
        var host = new Uri(job.Host);
        var account = accounts.FirstOrDefault(a => ReviewerConfig.SameHost(a.Host, host));
        var number = job.Number.ToString(CultureInfo.InvariantCulture);
        return account switch
        {
            null => null,
            { Provider: AccountConfig.GitLab } => new Uri($"{account.Host.AbsoluteUri.TrimEnd('/')}/{job.Repo}/-/merge_requests/{number}"),
            _ => new Uri($"{account.Host.AbsoluteUri.TrimEnd('/')}/{job.Repo}/pull/{number}"),
        };
    }

    [RelayCommand(CanExecute = nameof(HasPullRequestUrl))]
    private Task OpenPullRequestAsync() => shell.OpenUriAsync(PullRequestUrl!);

    private bool HasPullRequestUrl() => PullRequestUrl is not null;

    /// <summary>The attempt folder: the agent's prompt, its output and log, and <c>plan.json</c>.</summary>
    [RelayCommand(CanExecute = nameof(HasAttemptFolder))]
    private Task OpenFolderAsync() => shell.OpenFolderAsync(Job!.AttemptFolder!);

    [RelayCommand]
    private void Back() => shell.Back();
}
