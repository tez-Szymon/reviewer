using System.Globalization;
using System.Text;
using CliWrap;
using CliWrap.Buffered;

namespace Reviewer.Agents.Cli;

/// <param name="StandardInput">
/// Written to the process's standard input, which is then closed; null leaves it empty. The way to hand a process a
/// secret, since arguments show in every process listing.
/// </param>
public sealed record ProcessSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    string? StandardInput = null)
{
    // The input may be a secret, so a spec that ends up in a log or a test's output shows only whether there is one.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Executable = {Executable}, Arguments = [{string.Join(", ", Arguments)}], WorkingDirectory = {WorkingDirectory}");
        builder.Append(StandardInput is null ? "" : ", StandardInput = (hidden)");
        return true;
    }
}

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
        var command = CliWrap.Cli.Wrap(spec.Executable)
            .WithArguments(spec.Arguments)
            .WithWorkingDirectory(spec.WorkingDirectory)
            .WithEnvironmentVariables(spec.Environment)
            .WithValidation(CommandResultValidation.None);
        if (spec.StandardInput is { } input)
        {
            command = command.WithStandardInputPipe(PipeSource.FromString(input, Encoding.UTF8));
        }

        var result = await command
            .ExecuteBufferedAsync(Encoding.UTF8, ct)
            .ConfigureAwait(false);
        return new ProcessResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }
}
