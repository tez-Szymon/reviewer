using System.Globalization;
using Reviewer.Engine.Eval;

namespace Reviewer.Cli;

/// <summary>Prints <c>reviewer eval</c>: a line per case as it finishes, then the totals and the change since the last run.</summary>
internal static class EvalPrinter
{
    public static void PrintCase(EvalCaseResult result, TextWriter output)
    {
        var caught = result.Bugs.Count(b => b.Caught);
        var verdict = result.Error is not null
            ? "failed"
            : result.Bugs.Count == 0
                ? (result.Findings.Count == 0 ? "clean" : "noisy")
                : $"{caught}/{result.Bugs.Count} caught";
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{result.Case.Id,-28} {verdict,-12} {result.Other.Count,3} other  {Duration(result.Duration),7}"));
        if (result.Error is not null)
        {
            output.WriteLine($"    {result.Error}");
            return;
        }

        foreach (var bug in result.Bugs)
        {
            output.WriteLine(bug.CaughtBy is { } f
                ? $"    caught  {bug.Bug.Key}  as [{f.Severity.ToString().ToLowerInvariant()}] {f.Title}"
                : $"    missed  {bug.Bug.Key}  {bug.Bug.About}");
        }

        foreach (var f in result.Other)
        {
            output.WriteLine($"    other   {f.File}:{f.StartLine}  [{f.Severity.ToString().ToLowerInvariant()}] {f.Title}");
        }
    }

    public static void PrintRun(EvalRun run, EvalComparison? comparison, string savedTo, TextWriter output)
    {
        output.WriteLine();
        var share = run.Bugs == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $" ({100.0 * run.Caught / run.Bugs:0}%)");
        var failed = run.Failed > 0 ? $"; {run.Failed} case(s) failed" : "";
        output.WriteLine($"Caught {run.Caught} of {run.Bugs} known bug(s){share}, with {run.Other} other finding(s){failed}.");
        output.WriteLine($"Saved to {savedTo}");
        if (comparison is null)
        {
            output.WriteLine("No earlier run with this agent and model to compare with.");
            return;
        }

        output.WriteLine(
            $"Since the run of {comparison.Previous.Started.ToLocalTime():g} ({comparison.Previous.AppVersion}), on the cases both ran: " +
            $"caught {comparison.PreviousCaught} → {comparison.Caught} of {comparison.Bugs}, other findings {comparison.PreviousOther} → {comparison.Other}.");
        foreach (var bug in comparison.NowCaught)
        {
            output.WriteLine($"    now caught  {bug}");
        }

        foreach (var bug in comparison.NowMissed)
        {
            output.WriteLine($"    now missed  {bug}");
        }
    }

    private static string Duration(TimeSpan duration) => duration.TotalMinutes >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMinutes}m{duration.Seconds:00}s")
        : string.Create(CultureInfo.InvariantCulture, $"{duration.Seconds}s");
}
