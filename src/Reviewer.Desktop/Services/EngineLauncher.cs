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

    /// <summary>
    /// The running engine with this process id, if it's the CLI beside the app: an update replaces it, so it must be
    /// stopped first, wherever it was started.
    /// </summary>
    IEngineProcess? FindBundled(int processId);
}

internal sealed class EngineLaunchException(string message) : Exception(message);

/// <summary>
/// Starts <c>reviewer serve</c>, which appends its output to <c>engine.log</c> in the data folder itself. The CLI
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

        // The engine writes its own log, so nothing ties it to the app: it can keep running after the app quits.
        var start = new ProcessStartInfo(cli, ["serve", "--log", LogPath])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["REVIEWER_HOME"] = paths.Root;

        try
        {
            return new ChildProcess(Process.Start(start) ?? throw new EngineLaunchException($"{cli} didn't start."));
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new EngineLaunchException($"Couldn't start {cli}: {e.Message}");
        }
    }

    public IEngineProcess? FindBundled(int processId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            var path = process.MainModule?.FileName;
            if (path is not null && SamePath(path, Path.Combine(AppContext.BaseDirectory, CliName)))
            {
                var found = new ChildProcess(process);
                process = null;
                return found;
            }

            return null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Gone, or not ours to look at.
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

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

    private sealed class ChildProcess(Process process) : IEngineProcess
    {
        public bool HasExited => process.HasExited;

        public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);

        public void Kill() => process.Kill(entireProcessTree: true);

        public void Dispose() => process.Dispose();
    }
}
