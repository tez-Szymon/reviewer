using Reviewer.Agents.Cli;

namespace Reviewer.Cli.Tests;

/// <summary>Answers every call with one result, and records the calls.</summary>
internal sealed class FakeRunner(ProcessResult result) : IProcessRunner
{
    public List<ProcessSpec> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        Calls.Add(spec);
        return Task.FromResult(result);
    }
}

/// <summary>A keychain holding the given items, keyed by target.</summary>
internal sealed class FakeKeychain(Dictionary<string, string> items) : Reviewer.Cli.IKeychain
{
    public Task<string?> ReadAsync(Reviewer.Engine.Config.TokenReference.Keychain item, CancellationToken ct) =>
        Task.FromResult(items.GetValueOrDefault(item.Target));
}
