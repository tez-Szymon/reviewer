using Reviewer.Agents.Cli;
using Reviewer.Engine;
using Reviewer.Engine.Config;

namespace Reviewer.Cli.Tests;

public class PollSourceTests
{
    private static readonly AccountConfig GitLab = new("gl", AccountConfig.GitLab, new Uri("https://git.example.com"), "keychain:reviewer/gl");
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static PollSource Source(FakeKeychain keychain) =>
        new(new ReviewerPaths(Path.GetTempPath()), new FakeRunner(new ProcessResult(1, "", "")), keychain);

    [Fact]
    public async Task KeepsTheProvider_WhileTheTokenStaysTheSame()
    {
        using var source = Source(new FakeKeychain(new() { ["reviewer/gl"] = "glpat-one" }));

        Assert.Same(await source.GetProviderAsync(GitLab, Ct), await source.GetProviderAsync(GitLab, Ct));
    }

    [Fact]
    public async Task ConnectsAgain_WhenTheTokenIsReplacedWhereItsKept()
    {
        // As when the app saves a new token to the same keychain item after the first was rejected.
        var items = new Dictionary<string, string> { ["reviewer/gl"] = "glpat-rejected" };
        using var source = Source(new FakeKeychain(items));
        var first = await source.GetProviderAsync(GitLab, Ct);

        items["reviewer/gl"] = "glpat-works";

        Assert.NotSame(first, await source.GetProviderAsync(GitLab, Ct));
    }

    [Fact]
    public async Task SaysSo_WhenTheTokenIsGone()
    {
        var items = new Dictionary<string, string> { ["reviewer/gl"] = "glpat-one" };
        using var source = Source(new FakeKeychain(items));
        await source.GetProviderAsync(GitLab, Ct);

        items.Remove("reviewer/gl");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetProviderAsync(GitLab, Ct));
        Assert.Contains("keychain item reviewer/gl", e.Message, StringComparison.Ordinal);
    }
}
