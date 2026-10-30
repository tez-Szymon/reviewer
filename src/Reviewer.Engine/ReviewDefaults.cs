using Reviewer.Core.Findings;
using Reviewer.Core.Policy;
using Reviewer.Instructions;

namespace Reviewer.Engine;

/// <summary>The global defaults from the architecture's config example, used until config files arrive in M2.</summary>
public static class ReviewDefaults
{
    public static ReviewPolicy Policy { get; } = new(
        ReviewMode.Comment,
        DryRun: false,
        MinConfidence: 0.6,
        BlockingRules:
        [
            new BlockingRule(Severity.Major, [Category.Security, Category.Correctness]),
            new BlockingRule(Severity.Blocker),
        ],
        IgnorePaths: ["**/*.lock", "**/dist/**", "**/*.min.js"],
        MaxInlineComments: 20);

    public static TimeSpan Timeout { get; } = TimeSpan.FromMinutes(15);

    /// <summary>Default guide on, user skills from <c>skills/*.md</c> under the reviewer root, agent files as context.</summary>
    public static async Task<InstructionSettings> InstructionsAsync(ReviewerPaths paths, CancellationToken ct)
    {
        var skills = await SkillLoader.LoadAsync([Path.Combine(paths.Root, "skills", "*.md")], paths.Root, ct)
            .ConfigureAwait(false);
        return new InstructionSettings(UseDefaultGuide: true, skills, RepoSkills: [], RepoAgentFileMode.Context);
    }
}
