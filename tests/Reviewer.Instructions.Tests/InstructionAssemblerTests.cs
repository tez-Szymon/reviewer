namespace Reviewer.Instructions.Tests;

public sealed class InstructionAssemblerTests
{
    private static InstructionSources Sources(
        bool useDefaultGuide = true,
        IReadOnlyList<Skill>? userSkills = null,
        IReadOnlyList<Skill>? repoSkills = null,
        IReadOnlyList<RepoAgentFile>? agentFiles = null,
        RepoAgentFileMode mode = RepoAgentFileMode.Context) =>
        new(useDefaultGuide, userSkills ?? [], repoSkills ?? [], agentFiles ?? [], mode);

    private static Skill Skill(string name, string body, params string[] appliesTo) => new(name, appliesTo, [], body, $"{name}.md");

    [Fact]
    public void Assemble_PutsLayersInFixedOrder_WithContractFirstAndLast()
    {
        var text = InstructionAssembler.Assemble(
            Sources(
                userSkills: [Skill("user", "USER BODY")],
                repoSkills: [Skill("repo", "REPO BODY")],
                agentFiles: [new RepoAgentFile("CLAUDE.md", "AGENT BODY")]),
            ["src/a.cs"]);

        int[] positions =
        [
            text.IndexOf("# Core contract", StringComparison.Ordinal),
            text.IndexOf("# Review guide", StringComparison.Ordinal),
            text.IndexOf("USER BODY", StringComparison.Ordinal),
            text.IndexOf("REPO BODY", StringComparison.Ordinal),
            text.IndexOf("AGENT BODY", StringComparison.Ordinal),
            text.IndexOf("# Reminder", StringComparison.Ordinal),
        ];
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
        Assert.StartsWith("# Core contract", text, StringComparison.Ordinal);
        Assert.EndsWith(BuiltInInstructions.CoreContractRestated.Trim() + "\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Assemble_OmitsDefaultGuide_WhenDisabled()
    {
        var text = InstructionAssembler.Assemble(Sources(useDefaultGuide: false), ["a.cs"]);

        Assert.DoesNotContain("# Review guide", text, StringComparison.Ordinal);
        Assert.Contains("# Core contract", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Assemble_UsesSkill_OnlyWhenAppliesToMatchesAChangedFile()
    {
        var sources = Sources(userSkills: [Skill("sql", "SQL BODY", "**/*.sql"), Skill("cs", "CS BODY", "src/**/*.cs")]);

        var text = InstructionAssembler.Assemble(sources, ["src/Payments/Charge.cs", "README.md"]);

        Assert.Contains("CS BODY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SQL BODY", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Assemble_ShowsSkillFocus()
    {
        var skill = new Skill("payments", ["src/**"], ["security", "correctness"], "BODY", "payments.md");

        var text = InstructionAssembler.Assemble(Sources(userSkills: [skill]), ["src/a.cs"]);

        Assert.Contains("# User skill: payments", text, StringComparison.Ordinal);
        Assert.Contains("Focus: security, correctness", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RepoAgentFileMode.Context, "background")]
    [InlineData(RepoAgentFileMode.Instructions, "Apply them")]
    public void Assemble_FramesAgentFiles_ByMode(RepoAgentFileMode mode, string expected)
    {
        var text = InstructionAssembler.Assemble(Sources(agentFiles: [new RepoAgentFile("AGENTS.md", "NOTES")], mode: mode), ["a"]);

        Assert.Contains("# Repository file: AGENTS.md", text, StringComparison.Ordinal);
        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Assemble_LeavesOutAgentFiles_WhenIgnored()
    {
        var text = InstructionAssembler.Assemble(
            Sources(agentFiles: [new RepoAgentFile("CLAUDE.md", "NOTES")], mode: RepoAgentFileMode.Ignore), ["a"]);

        Assert.DoesNotContain("NOTES", text, StringComparison.Ordinal);
    }
}
