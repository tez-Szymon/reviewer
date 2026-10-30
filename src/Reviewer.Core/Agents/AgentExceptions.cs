namespace Reviewer.Core.Agents;

public abstract class AgentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The agent can't run: not installed, too old, or not logged in. Retrying won't help until the user acts.</summary>
public sealed class AgentUnavailableException(string message) : AgentException(message);

/// <summary>The subscription or API hit a usage or rate limit. The job should wait and retry.</summary>
public sealed class AgentThrottledException(string message, DateTimeOffset? retryAt) : AgentException(message)
{
    public DateTimeOffset? RetryAt { get; } = retryAt;
}

/// <summary>The agent ran but its output didn't match the findings schema, even after a repair round.</summary>
public sealed class AgentOutputException(string message, Exception? inner = null) : AgentException(message, inner);

/// <summary>The agent failed for another reason, including timeouts.</summary>
public sealed class AgentFailedException(string message, Exception? inner = null) : AgentException(message, inner);
