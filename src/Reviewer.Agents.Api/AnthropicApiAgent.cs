using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.AI;
using Reviewer.Core.Agents;
using Reviewer.Core.Findings;
using Reviewer.Git;
using Messages = Anthropic.Models.Messages;

namespace Reviewer.Agents.Api;

/// <param name="Model">The Claude model; current models only, since the agent asks for adaptive thinking.</param>
/// <param name="ApiKey">Reads the API key; null when there's none. The key never goes into a file or a log.</param>
/// <param name="MissingKey">What to tell the user when there's no key: where it was looked for, and how to add it.</param>
public sealed record AnthropicApiOptions(string Model, Func<CancellationToken, Task<string?>> ApiKey, string MissingKey)
{
    public const string DefaultModel = "claude-opus-5-5";
}

/// <summary>
/// Reviews with Claude through the Anthropic API, billed to the user's API key rather than a subscription. The engine
/// runs the tool loop (<see cref="ChatReview"/>) over the SDK's <see cref="IChatClient"/>, offering only the
/// worktree's read-only tools.
/// </summary>
/// <remarks>
/// Each request asks for adaptive thinking at high effort (code review is intelligence-sensitive), caches the growing
/// prompt, and names a fallback model, so a request the model's safeguards decline is served by the fallback
/// instead of failing the review. The SDK retries rate limits and overloads itself; what's still limited after that
/// becomes <see cref="AgentThrottledException"/>.
/// </remarks>
/// <param name="git">The repo the worktree came from, for <c>git_show</c>; null for health checks.</param>
public sealed class AnthropicApiAgent(AnthropicApiOptions options, IGitRepository? git = null, Func<string, IChatClient>? chatFactory = null) : IReviewAgent
{
    public const string AgentId = "anthropic-api";

    /// <summary>The model a declined request falls back to (the SDK's documented server-side fallback form).</summary>
    private static readonly Messages::Model FallbackModel = Messages::Model.ClaudeOpus4_8;

    public string Id => AgentId;

    public AgentCapabilities Capabilities { get; } = new(NeedsWorktree: true, MaxConcurrency: 1, SupportsSchemaFlag: true);

    /// <summary>Whether there's a key and the API accepts it for the model; costs a models lookup, no tokens.</summary>
    public async Task<AgentHealth> CheckAsync(CancellationToken ct)
    {
        if (await options.ApiKey(ct).ConfigureAwait(false) is not { } key)
        {
            return new AgentHealth(true, false, options.Model, options.MissingKey);
        }

        using var client = new AnthropicClient { ApiKey = key };
        try
        {
            await client.Models.Retrieve(options.Model, cancellationToken: ct).ConfigureAwait(false);
            return new AgentHealth(true, true, options.Model);
        }
        catch (Exception e) when (e is AnthropicUnauthorizedException or AnthropicForbiddenException)
        {
            return new AgentHealth(true, false, options.Model, "The Anthropic API refused the API key.");
        }
        catch (AnthropicNotFoundException)
        {
            return new AgentHealth(true, true, options.Model, $"The Anthropic API has no model '{options.Model}'.");
        }
        catch (Exception e) when (e is AnthropicException or HttpRequestException)
        {
            return new AgentHealth(true, true, options.Model, $"Couldn't reach the Anthropic API: {e.Message}");
        }
    }

    public async Task<FindingsReport> ReviewAsync(ReviewContext context, CancellationToken ct)
    {
        var key = await options.ApiKey(ct).ConfigureAwait(false) ?? throw new AgentUnavailableException(options.MissingKey);

        using var client = chatFactory is null ? new AnthropicClient { ApiKey = key } : null;
        using var chat = chatFactory?.Invoke(key) ?? client!.Beta.AsIChatClient(options.Model);
        var review = new ChatReview(chat, ChatOptions(options.Model), new WorktreeTools(context.WorktreePath, git), new ChatReviewLimits());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(context.Timeout);
        try
        {
            return await review.RunAsync(context.AttemptFolder, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new AgentFailedException($"The Anthropic API review timed out after {context.Timeout}.", e);
        }
        catch (Exception e) when (Classify(e) is { } classified)
        {
            throw classified;
        }
    }

    /// <summary>Thinking and effort for every request, and the beta parameters the chat client passes through.</summary>
    internal static ChatOptions ChatOptions(string model) => new()
    {
        Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
        RawRepresentationFactory = _ => new MessageCreateParams
        {
            Model = model,
            MaxTokens = new ChatReviewLimits().MaxOutputTokens,
            Messages = [],

            // Top-level caching: each round resends the conversation, so its prefix is read from the cache.
            CacheControl = new BetaCacheControlEphemeral(),
            Fallbacks = new([new(FallbackModel)]),
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
        },
    };

    /// <summary>What an API failure means for the job: wait, ask the user, or fail.</summary>
    internal static AgentException? Classify(Exception e) => e switch
    {
        AgentException => null,
        AnthropicUnauthorizedException or AnthropicForbiddenException =>
            new AgentUnavailableException("The Anthropic API refused the API key; check it, or the account's access."),
        AnthropicRateLimitException => new AgentThrottledException("The Anthropic API is rate limiting this key.", null),
        Anthropic5xxException => new AgentFailedException($"The Anthropic API failed: {Short(e.Message)}", e),
        AnthropicApiException api => new AgentFailedException($"The Anthropic API refused the request ({(int)api.StatusCode}): {Short(api.ResponseBody)}", e),
        AnthropicException or HttpRequestException => new AgentFailedException($"Couldn't reach the Anthropic API: {Short(e.Message)}", e),
        _ => null,
    };

    private static string Short(string text) => text.Length > 500 ? text[..500] : text;
}
