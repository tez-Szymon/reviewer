using CommunityToolkit.Mvvm.ComponentModel;
using Reviewer.Api;
using Reviewer.Engine.State;

namespace Reviewer.Desktop.ViewModels;

/// <summary>A job in a list, kept at its newest version as events arrive.</summary>
internal sealed partial class JobItem(JobDto job) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Pr), nameof(StateText), nameof(Tone), nameof(When), nameof(Error), nameof(CanCancel), nameof(CanRetry))]
    private JobDto _job = job;

    /// <summary>Finding counts from the list the item was loaded with; events don't carry them.</summary>
    public (int Inline, int SummaryOnly, int Blocking)? Counts { get; set; }

    public string Id => Job.Id;

    public string Pr => Describe.Pr(Job.Repo, Job.Number);

    public string StateText => Describe.State(Job);

    public Tone Tone => Describe.Tone(Job);

    public string When => Describe.Time(Job.StartedAt ?? Job.CreatedAt);

    public string? Error => Job.State is JobState.Failed or JobState.Cancelled or JobState.Throttled ? Job.Error : null;

    public bool CanCancel => Job.State is JobState.Queued or JobState.Throttled or JobState.Running && !Job.CancelRequested;

    public bool CanRetry => Job.State is JobState.Failed or JobState.Cancelled or JobState.Throttled;

    public string CountsText => Counts is var (inline, summary, blocking)
        ? $"{inline} inline, {summary} in summary{(blocking > 0 ? $", {blocking} blocking" : "")}"
        : "";

    /// <summary>Takes <paramref name="newer"/> unless this item already has a later version.</summary>
    public bool Update(JobDto newer)
    {
        if (newer.Version < Job.Version)
        {
            return false;
        }

        Job = newer;
        return true;
    }
}
