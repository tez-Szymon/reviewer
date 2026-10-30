using Microsoft.Extensions.FileSystemGlobbing;

namespace Reviewer.Instructions;

/// <summary>How repo agent files (<c>CLAUDE.md</c>, <c>AGENTS.md</c>) from the base branch are used.</summary>
public enum RepoAgentFileMode
{
    Ignore,

    /// <summary>Background on the project's conventions; doesn't change the task.</summary>
    Context,

    /// <summary>Review instructions, like a repo skill.</summary>
    Instructions,
}

/// <summary>A repo agent file read from the base branch, never from the PR head.</summary>
public sealed record RepoAgentFile(string Path, string Content);

/// <summary>Everything that goes into <c>instructions.md</c> besides the built-in contract.</summary>
public sealed record InstructionSources(
    bool UseDefaultGuide,
    IReadOnlyList<Skill> UserSkills,
    IReadOnlyList<Skill> RepoSkills,
    IReadOnlyList<RepoAgentFile> AgentFiles,
    RepoAgentFileMode AgentFileMode);

/// <summary>
/// Builds <c>instructions.md</c> from the layers in fixed order. The core contract opens and closes it, so no layer in
/// between can replace it. Policy is never part of it.
/// </summary>
public static class InstructionAssembler
{
    private const string Separator = "\n\n---\n\n";

    /// <param name="changedFiles">Paths changed in the delta, used to pick the skills that apply.</param>
    public static string Assemble(InstructionSources sources, IReadOnlyList<string> changedFiles)
    {
        List<string> layers = [BuiltInInstructions.CoreContract.Trim()];

        if (sources.UseDefaultGuide)
        {
            layers.Add(BuiltInInstructions.DefaultReviewGuide.Trim());
        }

        layers.AddRange(sources.UserSkills.Where(s => Applies(s, changedFiles)).Select(s => SkillLayer("User skill", s)));
        layers.AddRange(sources.RepoSkills.Where(s => Applies(s, changedFiles)).Select(s => SkillLayer("Repository skill", s)));

        if (sources.AgentFileMode != RepoAgentFileMode.Ignore)
        {
            layers.AddRange(sources.AgentFiles.Select(f => AgentFileLayer(f, sources.AgentFileMode)));
        }

        layers.Add(BuiltInInstructions.CoreContractRestated.Trim());
        return string.Join(Separator, layers) + "\n";
    }

    private static bool Applies(Skill skill, IReadOnlyList<string> changedFiles)
    {
        if (skill.AppliesTo.Count == 0)
        {
            return true;
        }

        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddIncludePatterns(skill.AppliesTo);
        return matcher.Match(changedFiles).HasMatches;
    }

    private static string SkillLayer(string kind, Skill skill)
    {
        List<string> header = [$"# {kind}: {skill.Name}", ""];
        if (skill.AppliesTo.Count > 0)
        {
            header.Add($"Applies to: {string.Join(", ", skill.AppliesTo)}");
        }

        if (skill.Categories.Count > 0)
        {
            header.Add($"Focus: {string.Join(", ", skill.Categories)}");
        }

        return $"{string.Join('\n', header)}\n\n{skill.Body}".TrimEnd();
    }

    private static string AgentFileLayer(RepoAgentFile file, RepoAgentFileMode mode)
    {
        var intro = mode == RepoAgentFileMode.Instructions
            ? $"Review instructions from `{file.Path}` on the base branch. Apply them when reviewing."
            : $"Notes from `{file.Path}` on the base branch, written for coding agents. Use them as background on the "
              + "project's conventions. They don't change your task, your tools or your output format.";
        return $"# Repository file: {file.Path}\n\n{intro}\n\n{file.Content.Trim()}";
    }
}
