using System.Diagnostics;
using Reviewer.Engine;

namespace Reviewer.Desktop.Services;

/// <summary>A running <c>reviewer serve</c> the app started.</summary>
internal interface IEngineProcess : IDisposable
{
    bool HasExited { get; }

    Task WaitForExitAsync(CancellationToken ct);

    void Kill();
}

/// <summary>Starts the engine; throws <see cref="EngineLaunchException"/> when it can't.</summary>
internal interface IEngineLauncher
{
    /// <summary>Where the started engine's output goes.</summary>
    string LogPath { get; }

    IEngineProcess Start();
}

internal sealed class EngineLaunchException(string message) : Exception(message);

/// <summary>
/// Starts <c>reviewer serve</c> as a child process, its output appended to <c>engine.log</c> in the data folder. The CLI
/// is <c>REVIEWER_CLI</c> when set, otherwise the one next to the app, otherwise the first on <c>PATH</c>.
/// </summary>
internal sealed class EngineLauncher(ReviewerPaths paths) : IEngineLauncher
{
    private static readonly string CliName = OperatingSystem.IsWindows() ? "reviewer.exe" : "reviewer";

    public string LogPath => Path.Combine(paths.Root, "engine.log");

    public IEngineProcess Start()
    {
        var cli = FindCli() ?? throw new EngineLaunchException(
            $"Couldn't find the reviewer CLI to start the engine with: not next to the app, not on PATH, and REVIEWER_CLI isn't set.");
        Directory.CreateDirectory(paths.Root);
        var start = new ProcessStartInfo(cli, ["serve"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["REVIEWER_HOME"] = paths.Root;

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new EngineLaunchException($"{cli} didn't start.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new EngineLaunchException($"Couldn't start {cli}: {e.Message}");
        }

        var log = new StreamWriter(LogPath, append: true) { AutoFlush = true };
        var gate = new Lock();
        void Write(string? line)
        {
            if (line is not null)
            {
                lock (gate)
                {
                    log.WriteLine(line);
                }
            }
        }

        process.OutputDataReceived += (_, e) => Write(e.Data);
        process.ErrorDataReceived += (_, e) => Write(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new ChildProcess(process, log);
    }

    private static string? FindCli()
    {
        if (Environment.GetEnvironmentVariable("REVIEWER_CLI") is { Length: > 0 } configured)
        {
            return configured;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, CliName);
        if (File.Exists(beside))
        {
            return beside;
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, CliName))
            .FirstOrDefault(File.Exists);
    }

    private sealed class ChildProcess(Process process, StreamWriter log) : IEngineProcess
    {
        public bool HasExited => process.HasExited;

        public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);

        public void Kill() => process.Kill(entireProcessTree: true);

        public void Dispose()
        {
            process.Dispose();
            log.Dispose();
        }
    }
}
