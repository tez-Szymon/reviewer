using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reviewer.Api;
using Reviewer.Core.Policy;
using Reviewer.Core.Providers;
using Reviewer.Desktop.Services;
using Reviewer.Engine.State;

namespace Reviewer.Desktop.ViewModels;

public enum OutcomeFilter
{
    All,
    Approved,
    ChangesRequested,
    Merged,
    Commented,
    Skipped,
    Failed,
    Cancelled,
}

/// <summary>Past and current jobs, newest first, filtered by repo and outcome.</summary>
internal sealed partial class HistoryViewModel(IEngineClient client, IShell shell) : PageViewModel
{
    /// <summary>How many of the newest jobs the page loads.</summary>
    public const int Limit = 500;

    private readonly LiveJobList _jobs = new(_ => true, (a, b) => b.Job.CreatedAt.CompareTo(a.Job.CreatedAt));

    [ObservableProperty]
    private string _repoFilter = "";

    [ObservableProperty]
    private OutcomeFilter _outcome = OutcomeFilter.All;

    public override string Title => "History";

    public static IReadOnlyList<OutcomeFilter> Outcomes { get; } = Enum.GetValues<OutcomeFilter>();

    /// <summary>The jobs that pass the filters.</summary>
    public ObservableCollection<JobItem> Jobs { get; } = [];

    public override async Task LoadAsync()
    {
        Loading = true;
        await TryAsync(() => _jobs.LoadAsync(async () =>
            [.. (await client.ListJobsAsync(null, null, Limit, CancellationToken.None)).Select(s => (s.Job, ((int, int, int)?)(s.Inline, s.SummaryOnly, s.Blocking)))]));
        Filter();
        Loading = false;
    }

    public override void OnChanged(JobDto job)
    {
        _jobs.OnChanged(job);
        Filter();
    }

    internal static bool Matches(JobDto job, string repo, OutcomeFilter outcome) =>
        (repo.Length == 0 || job.Repo.Contains(repo.Trim(), StringComparison.OrdinalIgnoreCase))
        && outcome switch
        {
            OutcomeFilter.All => true,
            OutcomeFilter.Approved => job.State == JobState.Succeeded && job.Decision == ReviewDecision.Approve,
            OutcomeFilter.ChangesRequested => job.State == JobState.Succeeded && job.Decision == ReviewDecision.RequestChanges,
            OutcomeFilter.Merged => job.Merge is not null,
            OutcomeFilter.Commented => job.State == JobState.Succeeded && job.Decision is null or ReviewDecision.None,
            OutcomeFilter.Skipped => job.State == JobState.Skipped,
            OutcomeFilter.Failed => job.State is JobState.Failed or JobState.Throttled,
            OutcomeFilter.Cancelled => job.State == JobState.Cancelled,
            _ => true,
        };

    partial void OnRepoFilterChanged(string value) => Filter();

    partial void OnOutcomeChanged(OutcomeFilter value) => Filter();

    private void Filter()
    {
        var visible = _jobs.Items.Where(i => Matches(i.Job, RepoFilter, Outcome)).ToList();
        if (!visible.SequenceEqual(Jobs))
        {
            Jobs.Clear();
            foreach (var item in visible)
            {
                Jobs.Add(item);
            }
        }
    }

    [RelayCommand]
    private void Open(JobItem job) => shell.OpenJob(job.Id);

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();
}
