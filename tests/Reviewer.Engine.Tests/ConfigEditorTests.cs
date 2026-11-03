using System.Text.Json.Nodes;
using Reviewer.Engine.Config;

namespace Reviewer.Engine.Tests;

public class ConfigEditorTests
{
    private const string File = """
        # Reviewer settings
        accounts:
          - { id: gh, provider: github }   # personal

        defaults:
          review:
            mode: comment    # start gently
            ignorePaths: ["**/*.lock"]
          triggers: { skipDrafts: true }

        repos:
          - repo: acme/api
            account: gh
          - { repo: acme/web, account: gh }
        # end
        """;

    private static string Edit(string yaml, params ConfigEdit[] edits) => ConfigEditor.Apply(yaml, edits);

    private static ConfigEdit Set(string path, JsonNode? value) => new(ConfigEditOp.Set, path, value);

    private static ConfigEdit Remove(string path) => new(ConfigEditOp.Remove, path);

    private static ConfigEdit Add(string path, JsonNode? value) => new(ConfigEditOp.Add, path, value);

    [Fact]
    public void Set_ReplacesAScalar_KeepingItsComment()
    {
        var result = Edit(File, Set("defaults.review.mode", "gate-merge"));

        Assert.Equal(File.Replace("mode: comment    # start gently", "mode: gate-merge    # start gently", StringComparison.Ordinal), result);
    }

    [Fact]
    public void Set_AddsAKey_AfterItsBlockSiblings()
    {
        var result = Edit(File, Set("defaults.review.dryRun", true));

        Assert.Contains("""
                ignorePaths: ["**/*.lock"]
                dryRun: true
              triggers:
            """, result, StringComparison.Ordinal);
        ConfigLoader.Parse(result);
    }

    [Fact]
    public void Set_AddsAKey_ToAFlowMap()
    {
        var result = Edit(File, Set("defaults.triggers.pollSeconds", 30));

        Assert.Contains("triggers: { skipDrafts: true, pollSeconds: 30 }", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_CreatesMissingMaps_InBlockStyle()
    {
        var result = Edit(File, Set("repos[0].overrides.review.mode", "gate"));

        Assert.Contains("""
                account: gh
                overrides:
                  review:
                    mode: gate
              - { repo: acme/web, account: gh }
            """, result, StringComparison.Ordinal);
        Assert.Equal(Core.Policy.ReviewMode.Gate, ConfigLoader.Parse(result).Repos[0].Policy.Review.Mode);
    }

    [Fact]
    public void Set_IntoAFlowListItem()
    {
        var result = Edit(File, Set("repos[1].agent", "codex"));

        Assert.Contains("- { repo: acme/web, account: gh, agent: codex }", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_ReplacesABlockMap_WithAFlowOne()
    {
        var result = Edit(File, Set("defaults.review", new JsonObject { ["mode"] = "gate" }));

        Assert.Contains("""
            defaults:
              review: { mode: gate }
              triggers: { skipDrafts: true }
            """, result, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_QuotesStringsYamlWouldReadAsSomethingElse()
    {
        var result = Edit("", Set("defaults.review.ignorePaths", new JsonArray("**/dist/**", "true", "12", "a: b")));

        Assert.Equal("""
            defaults:
              review:
                ignorePaths:
                  - "**/dist/**"
                  - "true"
                  - "12"
                  - "a: b"

            """, result);
    }

    [Fact]
    public void Set_InAnEmptyFile_AndOneWithOnlyComments()
    {
        Assert.Equal("agents:\n  default: codex\n", Edit("", Set("agents.default", "codex")));
        Assert.Equal("# mine\nagents:\n  default: codex\n", Edit("# mine\n", Set("agents.default", "codex")));
    }

    [Fact]
    public void Set_FillsAKeyWithNoValue()
    {
        var result = Edit("repos:\n  - repo: acme/api\n    overrides:\ndefaults: {}\n", Set("repos[0].overrides.agent", "x"));

        Assert.Equal("repos:\n  - repo: acme/api\n    overrides: { agent: x }\ndefaults: {}\n", result);
    }

    [Fact]
    public void Set_KeepsWindowsLineEndings()
    {
        var result = Edit("defaults:\r\n  review:\r\n    mode: comment\r\n", Set("defaults.review.dryRun", true));

        Assert.Equal("defaults:\r\n  review:\r\n    mode: comment\r\n    dryRun: true\r\n", result);
    }

    [Fact]
    public void Remove_ABlockKey_TakesItsLines()
    {
        var result = Edit(File, Remove("defaults.review.mode"));

        Assert.Contains("""
              review:
                ignorePaths: ["**/*.lock"]
            """, result, StringComparison.Ordinal);
        Assert.DoesNotContain("start gently", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_ABlockListItem_KeepsTheRest()
    {
        var result = Edit(File, Remove("repos[0]"));

        Assert.Contains("""
            repos:
              - { repo: acme/web, account: gh }
            # end
            """, result, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_TheLastEntry_LeavesAnEmptyMapOrList()
    {
        var result = Edit(File, Remove("accounts[0]"), Remove("defaults.triggers.skipDrafts"));

        Assert.Contains("accounts: []", result, StringComparison.Ordinal);
        Assert.Contains("triggers: {}", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a: { x: 1, y: 2, z: 3 }", "a.y", "a: { x: 1, z: 3 }")]
    [InlineData("a: { x: 1, y: 2 }", "a.y", "a: { x: 1 }")]
    [InlineData("a: [1, 2, 3]", "a[0]", "a: [2, 3]")]
    [InlineData("a: [1, 2, 3]", "a[2]", "a: [1, 2]")]
    public void Remove_FromFlowCollections(string yaml, string path, string expected)
    {
        Assert.Equal(expected, Edit(yaml, Remove(path)));
    }

    [Fact]
    public void Add_AppendsToABlockList_InFlowStyle()
    {
        var result = Edit(File, Add("repos", new JsonObject { ["repo"] = "acme/docs", ["account"] = "gh" }));

        Assert.Contains("""
              - { repo: acme/web, account: gh }
              - { repo: acme/docs, account: gh }
            # end
            """, result, StringComparison.Ordinal);
        Assert.Equal(3, ConfigLoader.Parse(result).Repos.Count);
    }

    [Fact]
    public void Add_ToAnIndentlessList()
    {
        Assert.Equal("repos:\n- a/b\n- c/d\n", Edit("repos:\n- a/b\n", Add("repos", "c/d")));
    }

    [Theory]
    [InlineData("labels: [a]", "labels: [a, b]")]
    [InlineData("labels: []", "labels: [b]")]
    [InlineData("labels:", "labels: [b]")]
    [InlineData("x: 1", "x: 1\nlabels:\n  - b")]
    public void Add_ToFlowEmptyOrMissingLists(string yaml, string expected)
    {
        Assert.Equal(expected, Edit(yaml, Add("labels", "b")));
    }

    [Theory]
    [InlineData("remove", "defaults.nothing", "defaults.nothing doesn't exist.")]
    [InlineData("set", "repos[5].agent", "repos[5].agent: repos[5] doesn't exist.")]
    [InlineData("set", "defaults.review.mode.x", "defaults.review.mode.x: defaults.review.mode isn't a map.")]
    [InlineData("add", "defaults.review", "defaults.review isn't a list.")]
    [InlineData("set", "defaults..mode", "'defaults..mode' isn't a config path")]
    public void BadPaths_SayWhy(string op, string path, string message)
    {
        var edit = new ConfigEdit(Enum.Parse<ConfigEditOp>(op, ignoreCase: true), path, "x");

        var e = Assert.Throws<ConfigException>(() => ConfigEditor.Apply(File, [edit]));

        Assert.StartsWith(message, Assert.Single(e.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidYaml_IsNotEdited()
    {
        var e = Assert.Throws<ConfigException>(() => Edit("a: [", Set("b", 1)));

        Assert.Contains("isn't valid YAML", Assert.Single(e.Errors), StringComparison.Ordinal);
    }
}
