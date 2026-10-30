using System.Globalization;
using Reviewer.Core.Findings;
using Reviewer.Engine;

namespace Reviewer.Cli;

/// <summary>Human-readable review outcome on stdout.</summary>
internal static class ReviewPrinter
{
    public static void Print(ReviewOutcome outcome, TextWriter output)
    {
        var plan = outcome.Plan;
        var pr = outcome.Snapshot.Ref;
        output.WriteLine($"{pr.Repo}#{pr.Number} at {outcome.Snapshot.HeadSha[..Math.Min(7, outcome.Snapshot.HeadSha.Length)]}");
        if (outcome.Skipped)
        {
            output.WriteLine($"Nothing new to review: {outcome.Delta.Reason} Use --full to review the whole PR again.");
            return;
        }

        output.WriteLine($"Reviewed: {(outcome.Delta.Kind == DeltaKind.Full ? "the whole PR" : outcome.Delta.Reason)}");
        output.WriteLine();
        output.WriteLine(plan.Summary.Trim());
        output.WriteLine();

        output.WriteLine($"Inline comments ({plan.InlineFindings.Count}):");
        foreach (var finding in plan.InlineFindings)
        {
            WriteFinding(output, finding, plan.BlockingFindingIds.Contains(finding.Id));
        }

        if (plan.SummaryFindings.Count > 0)
        {
            output.WriteLine($"In the summary only ({plan.SummaryFindings.Count}):");
            foreach (var finding in plan.SummaryFindings)
            {
                WriteFinding(output, finding, blocking: false);
            }
        }

        if (plan.AlreadyPosted.Count > 0)
        {
            output.WriteLine($"Already on the PR, not posted again ({plan.AlreadyPosted.Count}):");
            foreach (var finding in plan.AlreadyPosted)
            {
                WriteFinding(output, finding, plan.BlockingFindingIds.Contains(finding.Id));
            }
        }

        if (outcome.Resolved > 0 || outcome.StillOpen > 0)
        {
            output.WriteLine($"Earlier findings: {outcome.Resolved} fixed, {outcome.StillOpen} still open ({plan.OpenBlockingFindings.Count} blocking).");
        }

        var dropped = outcome.Report.Findings.Count - plan.InlineFindings.Count - plan.SummaryFindings.Count - plan.AlreadyPosted.Count;
        output.WriteLine();
        output.WriteLine($"Decision: {plan.Decision}{(dropped > 0 ? $" ({dropped} finding(s) filtered out)" : "")}");
        foreach (var reason in plan.Reasons)
        {
            output.WriteLine($"  - {reason}");
        }

        output.WriteLine();
        output.WriteLine(outcome.Published ? "Published to the pull request." : "Dry run: nothing was published.");
        output.WriteLine($"Job folder: {outcome.AttemptFolder}");
    }

    private static void WriteFinding(TextWriter output, Finding f, bool blocking)
    {
        var flag = blocking ? " BLOCKING" : "";
        output.WriteLine($"  [{f.Severity.ToString().ToLowerInvariant()}/{f.Category.ToString().ToLowerInvariant()} {f.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}{flag}] {f.File}:{f.StartLine} {f.Title}");
    }
}
