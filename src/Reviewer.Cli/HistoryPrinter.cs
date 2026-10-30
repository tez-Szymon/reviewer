using System.Globalization;
using Reviewer.Core.Providers;
using Reviewer.Engine;
using Reviewer.Engine.State;

namespace Reviewer.Cli;

/// <summary>Past jobs from the store, for <c>reviewer history</c> and <c>reviewer show</c>.</summary>
internal static class HistoryPrinter
{
    public static void PrintList(IReadOnlyList<JobSummary> jobs, TextWriter output)
    {
        if (jobs.Count == 0)
        {
            output.WriteLine("No reviews yet.");
            return;
        }

        foreach (var (job, inline, summary, blocking) in jobs)
        {
            var result = job.State == JobState.Succeeded
                ? $"{Outcome(job)}, {inline} inline, {summary} in summary{(blocking > 0 ? $", {blocking} blocking" : "")}"
                : State(job);
            output.WriteLine($"{job.Id}  {Time(job.CreatedAt)}  {job.Repo}#{job.Number}  {Short(job.HeadSha)}  {result}");
        }
    }

    public static void PrintJob(JobDetails details, TextWriter output)
    {
        var job = details.Job;
        output.WriteLine($"Job {job.Id}");
        output.WriteLine($"  PR:       {job.Host}/{job.Repo}/pull/{job.Number}");
        output.WriteLine($"  Head:     {job.HeadSha ?? "-"} (base {job.BaseSha ?? "-"})");
        if (job.Delta is { } delta)
        {
            output.WriteLine($"  Delta:    {delta}{(delta is DeltaKind.Unchanged ? "" : $" from {job.DeltaFrom}")}");
        }
        output.WriteLine($"  Started:  {Time(job.StartedAt ?? job.CreatedAt)}{(job.FinishedAt is { } end ? $", finished {Time(end)}" : "")}");
        output.WriteLine($"  Agent:    {job.AgentId}, config {job.ConfigHash}, reviewer {job.AppVersion}");
        output.WriteLine($"  Result:   {(job.State == JobState.Succeeded ? Outcome(job) : State(job))}");
        if (job.Error is { } error)
        {
            output.WriteLine($"  {(job.State == JobState.Skipped ? "Reason:  " : "Error:   ")} {error}");
        }

        if (job.AttemptFolder is { } folder)
        {
            output.WriteLine($"  Folder:   {folder}");
        }

        if (job.Summary is { } text)
        {
            output.WriteLine();
            output.WriteLine(text.Trim());
        }

        foreach (var disposition in Enum.GetValues<FindingDisposition>())
        {
            var findings = details.Findings.Where(f => f.Disposition == disposition).ToList();
            if (findings.Count == 0)
            {
                continue;
            }

            output.WriteLine();
            output.WriteLine($"{disposition switch
            {
                FindingDisposition.Inline => "Inline",
                FindingDisposition.Summary => "In the summary only",
                FindingDisposition.AlreadyPosted => "Already on the PR",
                _ => "Filtered out",
            }} ({findings.Count}):");
            foreach (var f in findings)
            {
                var flag = (f.Blocking ? " BLOCKING" : "") + (f.ResolvedByJobId is { } by ? $" fixed in {by}" : "");
                output.WriteLine($"  [{f.Severity.ToString().ToLowerInvariant()}/{f.Category.ToString().ToLowerInvariant()} {f.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}{flag}] {f.File}:{f.StartLine} {f.Title}");
            }
        }
    }

    private static string Outcome(JobRecord job)
    {
        var decision = job.Decision switch
        {
            ReviewDecision.Approve => "approved",
            ReviewDecision.RequestChanges => "requested changes",
            _ => "commented",
        };
        return job.Published ? decision : $"dry run ({decision})";
    }

    private static string State(JobRecord job) => job.State switch
    {
        JobState.Throttled when job.RetryAt is { } at => $"throttled until {Time(at)}",
        JobState.Skipped => "skipped, nothing new",
        _ => job.State.ToString().ToLowerInvariant(),
    };

    private static string Time(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Short(string? sha) => sha is null ? "-------" : sha[..Math.Min(7, sha.Length)];
}
