using System.Globalization;
using Reviewer.Api;
using Reviewer.Core.Policy;
using Reviewer.Core.Providers;
using Reviewer.Engine.State;

namespace Reviewer.Desktop.ViewModels;

/// <summary>Words for jobs, as <c>reviewer watch</c> and <c>history</c> print them.</summary>
internal static class Describe
{
    public static string State(JobDto job) => job.State switch
    {
        JobState.Succeeded => Outcome(job),
        JobState.Running when job.CancelRequested => "stopping",
        JobState.Throttled when job.RetryAt is { } at => $"throttled until {Time(at)}",
        JobState.Queued when job.NotBefore is { } at && at > DateTimeOffset.UtcNow => $"queued, starts {Time(at)}",
        JobState.Skipped => "skipped, nothing new",
        _ => Name(job.State),
    };

    public static string Outcome(JobDto job)
    {
        var name = job.Decision switch
        {
            ReviewDecision.Approve => "approved",
            ReviewDecision.RequestChanges => "requested changes",
            _ => "commented",
        } + job.Merge switch
        {
            MergeIntent.Merge => ", merged",
            MergeIntent.MergeWhenGreen => ", merging when CI passes",
            _ => "",
        };
        return job.Published ? name : $"dry run ({name})";
    }

    public static string Name(JobState state) => state.ToString().ToLowerInvariant();

    public static string Pr(string repo, int number) => $"{repo}#{number.ToString(CultureInfo.InvariantCulture)}";

    public static string Short(string? sha) => sha is { Length: > 7 } ? sha[..7] : sha ?? "";

    public static string Time(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    }
}
