using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Reviewer.Api;
using Reviewer.Desktop.Services;
using Reviewer.Engine.State;

namespace Reviewer.Desktop.ViewModels;

/// <summary>Queued, running and throttled jobs, oldest first: what <c>reviewer queue</c> lists.</summary>
internal sealed partial class QueueViewModel(IEngineClient client, IShell shell) : PageViewModel
{
    private readonly LiveJobList _jobs = new(
        j => j.State is JobState.Queued or JobState.Running or JobState.Throttled,
        (a, b) => a.Job.CreatedAt.CompareTo(b.Job.CreatedAt));

    public override string Title => "Queue";

    public ObservableCollection<JobItem> Jobs => _jobs.Items;

    public override async Task LoadAsync()
    {
        Loading = true;
        await TryAsync(() => _jobs.LoadAsync(async () =>
            [.. (await client.GetQueueAsync(CancellationToken.None)).Select(j => (j, ((int, int, int)?)null))]));
        Loading = false;
    }

    public override void OnChanged(JobDto job) => _jobs.OnChanged(job);

    [RelayCommand]
    private Task CancelAsync(JobItem job) => TryAsync(() => client.CancelJobAsync(job.Id, CancellationToken.None));

    [RelayCommand]
    private Task RetryAsync(JobItem job) => TryAsync(() => client.RetryJobAsync(job.Id, CancellationToken.None));

    [RelayCommand]
    private void Open(JobItem job) => shell.OpenJob(job.Id);
}
