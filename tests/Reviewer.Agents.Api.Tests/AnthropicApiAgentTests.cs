using System.Net;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.AI;
using Reviewer.Core.Agents;

namespace Reviewer.Agents.Api.Tests;

public sealed class AnthropicApiAgentTests : IDisposable
{
    private const string Valid = """{ "summary": "Looks fine.", "findings": [] }""";

    private readonly Workspace _space = new();

    public void Dispose() => _space.Dispose();

    private static AnthropicApiOptions Options(string? key) =>
        new(AnthropicApiOptions.DefaultModel, _ => Task.FromResult(key), "No API key: set ANTHROPIC_API_KEY.");

    private ReviewContext Context(TimeSpan? timeout = null) => new(_space.Worktree, _space.Attempt, timeout ?? TimeSpan.FromMinutes(1));

    [Fact]
    public async Task WithoutAKey_ItIsUnavailable_AndSaysWhereToPutOne()
    {
        var agent = new AnthropicApiAgent(Options(null), chatFactory: _ => throw new InvalidOperationException("no client without a key"));

        var health = await agent.CheckAsync(CancellationToken.None);
        var e = await Assert.ThrowsAsync<AgentUnavailableException>(() => agent.ReviewAsync(Context(), CancellationToken.None));

        Assert.False(health.IsReady);
        Assert.Equal("No API key: set ANTHROPIC_API_KEY.", health.Problem);
        Assert.Equal("No API key: set ANTHROPIC_API_KEY.", e.Message);
        Assert.Equal("anthropic-api", agent.Id);
    }

    [Fact]
    public async Task Review_UsesTheKeyForItsClient_AndReturnsTheReport()
    {
        string? usedKey = null;
        var chat = new ScriptedChat(ScriptedChat.Text(Valid));
        var agent = new AnthropicApiAgent(Options("sk-ant-test"), chatFactory: key =>
        {
            usedKey = key;
            return chat;
        });

        var report = await agent.ReviewAsync(Context(), CancellationToken.None);

        Assert.Equal("Looks fine.", report.Summary);
        Assert.Equal("sk-ant-test", usedKey);
        Assert.Equal(ReasoningEffort.High, chat.Requests[0].Options!.Reasoning!.Effort);
        Assert.DoesNotContain("sk-ant-test", await File.ReadAllTextAsync(Path.Combine(_space.Attempt, ChatReview.LogFile)), StringComparison.Ordinal);
    }

    [Fact]
    public void Requests_CacheThePrompt_AndFallBackWhenDeclined()
    {
        var options = AnthropicApiAgent.ChatOptions("claude-opus-5-5");

        var raw = Assert.IsType<MessageCreateParams>(options.RawRepresentationFactory!(new ScriptedChat()));

        Assert.Equal("claude-opus-5-5", raw.Model.Raw());
        Assert.NotNull(raw.CacheControl);
        Assert.NotNull(raw.Fallbacks);
        Assert.Contains(raw.Betas!, b => b.Raw().StartsWith("server-side-fallback-", StringComparison.Ordinal));
        Assert.Equal(ReasoningEffort.High, options.Reasoning!.Effort);
    }

    [Fact]
    public async Task Review_ThatRunsPastItsTimeout_Fails()
    {
        var agent = new AnthropicApiAgent(Options("sk-ant-test"), chatFactory: _ => new SlowChat());

        var e = await Assert.ThrowsAsync<AgentFailedException>(() => agent.ReviewAsync(Context(TimeSpan.FromMilliseconds(50)), CancellationToken.None));

        Assert.StartsWith("The Anthropic API review timed out", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_MapToWhatTheJobDoesNext()
    {
        Assert.IsType<AgentThrottledException>(AnthropicApiAgent.Classify(new AnthropicRateLimitException { StatusCode = HttpStatusCode.TooManyRequests, ResponseBody = "" }));
        Assert.IsType<AgentUnavailableException>(AnthropicApiAgent.Classify(new AnthropicUnauthorizedException { StatusCode = HttpStatusCode.Unauthorized, ResponseBody = "" }));
        Assert.IsType<AgentUnavailableException>(AnthropicApiAgent.Classify(new AnthropicForbiddenException { StatusCode = HttpStatusCode.Forbidden, ResponseBody = "" }));
        var bad = Assert.IsType<AgentFailedException>(AnthropicApiAgent.Classify(
            new AnthropicBadRequestException { StatusCode = HttpStatusCode.BadRequest, ResponseBody = "max_tokens too large" }));
        Assert.Contains("(400): max_tokens too large", bad.Message, StringComparison.Ordinal);
        Assert.IsType<AgentFailedException>(AnthropicApiAgent.Classify(new HttpRequestException("connection refused")));
        Assert.Null(AnthropicApiAgent.Classify(new AgentOutputException("invalid")));
        Assert.Null(AnthropicApiAgent.Classify(new InvalidOperationException("a bug of ours")));
    }

    private sealed class SlowChat : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(System.Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
