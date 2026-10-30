using System.Collections;

namespace Reviewer.Agents.Cli.Tests;

public class AgentEnvironmentTests
{
    [Theory]
    [InlineData("GITHUB_TOKEN", true)]
    [InlineData("GH_TOKEN", true)]
    [InlineData("GITLAB_PRIVATE_TOKEN", true)]
    [InlineData("AWS_SECRET_ACCESS_KEY", true)]
    [InlineData("OPENAI_API_KEY", true)]
    [InlineData("NPM_CONFIG__AUTHTOKEN", true)]
    [InlineData("DB_PASSWORD", true)]
    [InlineData("SSH_AUTH_SOCK", true)]
    [InlineData("GIT_ASKPASS", true)]
    [InlineData("ANTHROPIC_API_KEY", false)]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN", false)]
    [InlineData("PATH", false)]
    [InlineData("HOME", false)]
    [InlineData("KEYBOARD_LAYOUT", false)]
    public void IsSecret_MatchesCredentialNames(string name, bool secret)
    {
        Assert.Equal(secret, AgentEnvironment.IsSecret(name));
    }

    [Fact]
    public void Scrubbed_UnsetsSecrets_AndAppliesOverrides()
    {
        var current = new Hashtable { ["PATH"] = "/bin", ["GH_TOKEN"] = "x" };

        var env = AgentEnvironment.Scrubbed(current, new Dictionary<string, string?> { ["LC_ALL"] = "C" });

        Assert.Equal(new Dictionary<string, string?> { ["GH_TOKEN"] = null, ["LC_ALL"] = "C" }, env);
    }
}
