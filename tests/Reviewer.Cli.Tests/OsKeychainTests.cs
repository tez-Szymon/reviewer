using Reviewer.Agents.Cli;
using Reviewer.Engine.Config;

namespace Reviewer.Cli.Tests;

// The process-based paths: security(1) on macOS, secret-tool elsewhere. On Windows, which calls CredReadW directly, they pass vacuously.
public class OsKeychainTests
{
    private static readonly TokenReference.Keychain Item = new("reviewer", "gh-main");
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static bool UsesTools => !OperatingSystem.IsWindows();

    [Fact]
    public async Task Read_Found_ReturnsTheSecretWithoutTheTrailingNewline()
    {
        if (!UsesTools)
        {
            return;
        }

        var runner = new FakeRunner(new ProcessResult(0, "ghp_abc\n", ""));

        Assert.Equal("ghp_abc", await new OsKeychain(runner).ReadAsync(Item, Ct));

        var call = Assert.Single(runner.Calls);
        Assert.Equal(
            OperatingSystem.IsMacOS()
                ? ["find-generic-password", "-s", "reviewer", "-a", "gh-main", "-w"]
                : (string[])["lookup", "service", "reviewer", "account", "gh-main"],
            call.Arguments);
        Assert.Empty(call.Environment);
    }

    [Fact]
    public async Task Read_NotFound_ReturnsNull()
    {
        if (!UsesTools)
        {
            return;
        }

        var notFound = OperatingSystem.IsMacOS()
            ? new ProcessResult(44, "", "security: SecKeychainSearchCopyNext: The specified item could not be found in the keychain.")
            : new ProcessResult(1, "", "");

        Assert.Null(await new OsKeychain(new FakeRunner(notFound)).ReadAsync(Item, Ct));
    }

    [Fact]
    public async Task Read_Denied_ThrowsWithTheToolsMessage()
    {
        if (!UsesTools)
        {
            return;
        }

        var runner = new FakeRunner(new ProcessResult(51, "", "User interaction is not allowed."));

        var e = await Assert.ThrowsAsync<KeychainException>(() => new OsKeychain(runner).ReadAsync(Item, Ct));

        Assert.Contains("keychain item reviewer/gh-main", e.Message, StringComparison.Ordinal);
        Assert.Contains("User interaction is not allowed.", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_HandsTheSecretOverOnStandardInput_ThenReadsItBack()
    {
        if (!UsesTools)
        {
            return;
        }

        var runner = new FakeRunner(new ProcessResult(0, "glpat-abc_1.2\n", ""));

        await new OsKeychain(runner).WriteAsync(Item, "glpat-abc_1.2", Ct);

        Assert.Equal(2, runner.Calls.Count);
        var write = runner.Calls[0];
        Assert.DoesNotContain(write.Arguments, a => a.Contains("glpat", StringComparison.Ordinal));
        Assert.DoesNotContain("glpat", write.ToString(), StringComparison.Ordinal);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(["-i"], write.Arguments);
            Assert.Equal("add-generic-password -U -s \"reviewer\" -a \"gh-main\" -w \"glpat-abc_1.2\"\n", write.StandardInput);
        }
        else
        {
            Assert.Equal(["store", "--label=reviewer/gh-main", "service", "reviewer", "account", "gh-main"], write.Arguments);
            Assert.Equal("glpat-abc_1.2", write.StandardInput);
        }
    }

    [Fact]
    public async Task Write_ThatDoesntReadBack_Throws()
    {
        if (!UsesTools)
        {
            return;
        }

        // security -i exits 0 even when its command fails; the item then reads back as something else, or not at all.
        var e = await Assert.ThrowsAsync<KeychainException>(
            () => new OsKeychain(new FakeRunner(new ProcessResult(0, "", ""))).WriteAsync(Item, "glpat-abc", Ct));

        Assert.Contains("doesn't read back", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("glpat abc")]
    [InlineData("glpat-abc\n")]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    public async Task Write_RefusesWhatIsntAToken_WithoutRunningAnything(string secret)
    {
        var runner = new FakeRunner(new ProcessResult(0, "", ""));

        await Assert.ThrowsAsync<KeychainException>(() => new OsKeychain(runner).WriteAsync(Item, secret, Ct));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void AddCommand_PromptsForTheTokenInsteadOfTakingIt()
    {
        var command = OsKeychain.AddCommand(Item);

        Assert.Contains("reviewer", command, StringComparison.Ordinal);
        Assert.Contains("gh-main", command, StringComparison.Ordinal);
        // Each tool prompts when the secret is left off: security's trailing -w, cmdkey's bare /pass, secret-tool always.
        Assert.Matches(@"( -w| /pass|^secret-tool store .*)$", command);
    }
}
