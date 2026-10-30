namespace Reviewer.Engine.Tests;

/// <summary>A clock that moves only when a test sets it.</summary>
internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
