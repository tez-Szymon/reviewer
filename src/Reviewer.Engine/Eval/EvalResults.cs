using System.Globalization;
using System.Text.Json;
using Reviewer.Core.Findings;

namespace Reviewer.Engine.Eval;

/// <summary>One finding as a run file keeps it.</summary>
public sealed record EvalFindingRecord(string Severity, double Confidence, string File, int StartLine, int EndLine, string Title);

/// <summary>One case of a run, as the run file keeps it.</summary>
/// <param name="Caught">Keys of the known bugs a finding reported (<see cref="KnownBug.Key"/>).</param>
/// <param name="Missed">Keys of the known bugs no finding reported.</param>
public sealed record EvalCaseRecord(
    string Id,
    IReadOnlyList<string> Caught,
    IReadOnlyList<string> Missed,
    IReadOnlyList<EvalFindingRecord> Other,
    int Dropped,
    double Seconds,
    string? AttemptFolder,
    string? Error)
{
    public static EvalCaseRecord From(EvalCaseResult result) => new(
        result.Case.Id,
        [.. result.Bugs.Where(b => b.Caught).Select(b => b.Bug.Key)],
        [.. result.Bugs.Where(b => !b.Caught).Select(b => b.Bug.Key)],
        [.. result.Other.Select(Record)],
        result.Dropped,
        Math.Round(result.Duration.TotalSeconds, 1),
        result.AttemptFolder,
        result.Error);

    private static EvalFindingRecord Record(Finding f) =>
        new(f.Severity.ToString().ToLowerInvariant(), f.Confidence, f.File, f.StartLine, f.EndLine, f.Title);
}

/// <summary>A whole eval run, saved to the eval's results folder so later runs can be compared with it.</summary>
/// <param name="Model">The agent's configured model; null when the agent picks its own.</param>
/// <param name="Complete">Every case of the set was run, not a <c>--case</c> selection or a run cut short.</param>
public sealed record EvalRun(
    string Agent,
    string? Model,
    string AppVersion,
    DateTimeOffset Started,
    bool Complete,
    IReadOnlyList<EvalCaseRecord> Cases)
{
    public int Bugs => Cases.Sum(c => c.Caught.Count + c.Missed.Count);

    public int Caught => Cases.Sum(c => c.Caught.Count);

    public int Other => Cases.Sum(c => c.Other.Count);

    public int Failed => Cases.Count(c => c.Error is not null);
}

/// <summary>What changed between two runs, over the cases both ran.</summary>
/// <param name="NowCaught">Bugs the earlier run missed and this one caught, as <c>case: key</c>.</param>
/// <param name="NowMissed">Bugs the earlier run caught and this one missed.</param>
public sealed record EvalComparison(
    EvalRun Previous,
    int PreviousCaught,
    int Caught,
    int Bugs,
    int PreviousOther,
    int Other,
    IReadOnlyList<string> NowCaught,
    IReadOnlyList<string> NowMissed);

/// <summary>Run files in the eval's results folder: one JSON file per run, named by start time and agent.</summary>
public static class EvalResults
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <returns>The file the run was saved to.</returns>
    public static async Task<string> SaveAsync(string folder, EvalRun run, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var name = $"{run.Started.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{run.Agent}";
        var path = Path.Combine(folder, name + ".json");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{name}-{n}.json");
        }

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(run, Json), ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// The latest earlier run with the same agent and model that ran the cases <paramref name="run"/> ran, if any.
    /// Files that can't be read are skipped.
    /// </summary>
    public static async Task<EvalRun?> FindPreviousAsync(string folder, EvalRun run, CancellationToken ct)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var ids = run.Cases.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        EvalRun? latest = null;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            EvalRun? earlier;
            try
            {
                earlier = JsonSerializer.Deserialize<EvalRun>(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false), Json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (earlier is { Cases: not null }
                && earlier.Started < run.Started
                && earlier.Agent == run.Agent
                && earlier.Model == run.Model
                && earlier.Cases.Any(c => ids.Contains(c.Id))
                && (latest is null || earlier.Started > latest.Started))
            {
                latest = earlier;
            }
        }

        return latest;
    }

    /// <summary>Compares the cases both runs reviewed without an error, so a failed case reads as neither caught nor missed.</summary>
    public static EvalComparison Compare(EvalRun previous, EvalRun run)
    {
        var before = previous.Cases.Where(c => c.Error is null).ToDictionary(c => c.Id, StringComparer.Ordinal);
        var both = run.Cases.Where(c => c.Error is null && before.ContainsKey(c.Id)).ToList();
        var nowCaught = both.SelectMany(c => c.Caught.Where(k => before[c.Id].Missed.Contains(k)).Select(k => $"{c.Id}: {k}")).ToList();
        var nowMissed = both.SelectMany(c => c.Missed.Where(k => before[c.Id].Caught.Contains(k)).Select(k => $"{c.Id}: {k}")).ToList();
        return new EvalComparison(
            previous,
            both.Sum(c => before[c.Id].Caught.Count),
            both.Sum(c => c.Caught.Count),
            both.Sum(c => c.Caught.Count + c.Missed.Count),
            both.Sum(c => before[c.Id].Other.Count),
            both.Sum(c => c.Other.Count),
            nowCaught,
            nowMissed);
    }
}
