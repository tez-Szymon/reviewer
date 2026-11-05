using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Reviewer.Api;
using Reviewer.Desktop.Services;
using Reviewer.Engine.State;

namespace Reviewer.Desktop.ViewModels;

/// <summary>Queued, running and throttled jobs, oldest first: what <c>reviewer queue</c> lists.</summary>
internal sealed partial class QueueViewModel : PageViewModel
{
    private readonly IEngineClient _client;
    private readonly IShell _shell;
    private readonly LiveJobList _jobs = new(
        j => j.State is JobState.Queued or JobState.Running or JobState.Throttled,
        (a, b) => a.Job.CreatedAt.CompareTo(b.Job.CreatedAt));

    public QueueViewModel(IEngineClient client, IShell shell)
    {
        _client = client;
        _shell = shell;

        // An updated job is taken out and put back in order, so every change to the queue passes through here.
        _jobs.Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Activity));
            OnPropertyChanged(nameof(Badge));
        };
    }

    /// <summary>What the queue holds, in a few words: "1 reviewing, 2 queued", or "idle".</summary>
    public string Activity
    {
        get
        {
            var jobs = _jobs.Items.Select(i => i.Job).ToList();
            var parts = new[]
            {
                (jobs.Count(j => j.State == JobState.Running), "reviewing"),
                (jobs.Count(j => j.State == JobState.Queued), "queued"),
                (jobs.Count(j => j.State == JobState.Throttled), "throttled"),
            }.Where(p => p.Item1 > 0).Select(p => $"{p.Item1} {p.Item2}").ToList();
            return parts.Count == 0 ? "idle" : string.Join(", ", parts);
        }
    }

    public override string Title => "Queue";

    /// <summary>How many jobs wait or run, while there are any.</summary>
    public override string? Badge => Jobs.Count > 0 ? Jobs.Count.ToString(CultureInfo.InvariantCulture) : null;

    public ObservableCollection<JobItem> Jobs => _jobs.Items;

    public override async Task LoadAsync()
    {
        Loading = true;
        await TryAsync(() => _jobs.LoadAsync(async () =>
            [.. (await _client.GetQueueAsync(CancellationToken.None)).Select(j => (j, ((int, int, int)?)null))]));
        Loading = false;
    }

    public override void OnChanged(JobDto job) => _jobs.OnChanged(job);

    [RelayCommand]
    private Task CancelAsync(JobItem job) => TryAsync(() => _client.CancelJobAsync(job.Id, CancellationToken.None));

    [RelayCommand]
    private Task RetryAsync(JobItem job) => TryAsync(() => _client.RetryJobAsync(job.Id, CancellationToken.None));

    [RelayCommand]
    private void Open(JobItem job) => _shell.OpenJob(job.Id);
}
