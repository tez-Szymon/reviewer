using Reviewer.Engine.Config;

namespace Reviewer.Engine.Tests;

public class TokenReferenceTests
{
    [Fact]
    public void Parse_Env_ReadsTheVariableName()
    {
        Assert.Equal(new TokenReference.Env("GITHUB_TOKEN"), TokenReference.Parse("env:GITHUB_TOKEN"));
    }

    [Fact]
    public void Parse_Keychain_SplitsServiceAndAccount()
    {
        var item = Assert.IsType<TokenReference.Keychain>(TokenReference.Parse("keychain:reviewer/gl-work"));

        Assert.Equal(("reviewer", "gl-work"), (item.Service, item.Account));
        Assert.Equal("reviewer/gl-work", item.Target);
        Assert.Equal("keychain item reviewer/gl-work", item.Describe());
    }

    [Theory]
    [InlineData("ghp_secret123")]
    [InlineData("env:1BAD")]
    [InlineData("env:")]
    [InlineData("keychain:")]
    [InlineData("keychain:only-service")]
    [InlineData("keychain:a/b/c")]
    [InlineData("keychain:a b/c")]
    [InlineData("Keychain:a/b")]
    public void TryParse_NotAReference_Fails(string value)
    {
        Assert.False(TokenReference.TryParse(value, out _));
    }
}
