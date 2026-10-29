using System.Text;
using CliWrap;
using CliWrap.Buffered;

namespace Reviewer.Agents.Cli;

public sealed record ProcessSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IProcessRunner
{
    /// <summary>Runs to completion. Cancelling kills the process tree.</summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct);
}

public sealed class CliWrapProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        var result = await CliWrap.Cli.Wrap(spec.Executable)
            .WithArguments(spec.Arguments)
            .WithWorkingDirectory(spec.WorkingDirectory)
            .WithEnvironmentVariables(spec.Environment)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(Encoding.UTF8, ct)
            .ConfigureAwait(false);
        return new ProcessResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }
}
