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
    public void AddCommand_PromptsForTheTokenInsteadOfTakingIt()
    {
        var command = OsKeychain.AddCommand(Item);

        Assert.Contains("reviewer", command, StringComparison.Ordinal);
        Assert.Contains("gh-main", command, StringComparison.Ordinal);
        // Each tool prompts when the secret is left off: security's trailing -w, cmdkey's bare /pass, secret-tool always.
        Assert.Matches(@"( -w| /pass|^secret-tool store .*)$", command);
    }
}
