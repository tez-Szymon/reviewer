namespace Reviewer.Instructions.Tests;

public sealed class SkillTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("reviewer-skill-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public void Parse_ReadsFrontmatter_AndStripsItFromBody()
    {
        var skill = Skill.Parse("/skills/x.md", """
            ---
            name: "Payments rules"
            appliesTo: ["src/Payments/**", '**/*.sql']
            categories: [security, correctness]
            owner: ignored
            ---
            Check idempotency keys.
            """);

        Assert.Equal("Payments rules", skill.Name);
        Assert.Equal(["src/Payments/**", "**/*.sql"], skill.AppliesTo);
        Assert.Equal(["security", "correctness"], skill.Categories);
        Assert.Equal("Check idempotency keys.", skill.Body);
    }

    [Fact]
    public void Parse_WithoutFrontmatter_UsesFileName_AndWholeText()
    {
        var skill = Skill.Parse("/skills/naming.md", "# Naming\r\nUse nouns.\r\n");

        Assert.Equal("naming", skill.Name);
        Assert.Empty(skill.AppliesTo);
        Assert.Equal("# Naming\r\nUse nouns.", skill.Body);
    }

    [Fact]
    public void Parse_AcceptsScalarAppliesTo()
    {
        var skill = Skill.Parse("a.md", "---\nappliesTo: src/**/*.cs\n---\nbody");

        Assert.Equal(["src/**/*.cs"], skill.AppliesTo);
    }

    [Fact]
    public void Parse_ReadsBlockLists()
    {
        var skill = Skill.Parse("a.md", """
            ---
            appliesTo:
              - "src/**/*.{cs,ts}"
              - tests/**
            categories:
            - security
            name: sec
            ---
            body
            """);

        Assert.Equal(["src/**/*.{cs,ts}", "tests/**"], skill.AppliesTo);
        Assert.Equal(["security"], skill.Categories);
        Assert.Equal("sec", skill.Name);
        Assert.Equal("body", skill.Body);
    }

    [Fact]
    public async Task Load_ExpandsHome_MatchesGlobs_InPathOrder()
    {
        var skills = Directory.CreateDirectory(Path.Combine(_root, ".reviewer", "skills")).FullName;
        Directory.CreateDirectory(Path.Combine(skills, "payments"));
        await File.WriteAllTextAsync(Path.Combine(skills, "b.md"), "B", Ct);
        await File.WriteAllTextAsync(Path.Combine(skills, "a.md"), "A", Ct);
        await File.WriteAllTextAsync(Path.Combine(skills, "notes.txt"), "not a skill", Ct);
        await File.WriteAllTextAsync(Path.Combine(skills, "payments", "p.md"), "P", Ct);

        var loaded = await SkillLoader.LoadAsync(["~/.reviewer/skills/*.md", "~/.reviewer/skills/payments/p.md"], _root, Ct);

        Assert.Equal(["a", "b", "p"], loaded.Select(s => s.Name));
    }

    [Fact]
    public async Task Load_SkipsMissingDirectoriesAndFiles()
    {
        var loaded = await SkillLoader.LoadAsync(["~/nope/*.md", "~/nope.md"], _root, Ct);

        Assert.Empty(loaded);
    }
}
