using CommunityToolkit.Mvvm.ComponentModel;
using Reviewer.Api;

namespace Reviewer.Desktop.Services;

public enum EngineState
{
    Connecting,
    Starting,
    Connected,
    Unavailable,
}

/// <summary>
/// The app's link to the engine: it finds one listening on the socket, or starts <c>reviewer serve</c> and waits for
/// its API. An engine the app started is stopped when the app quits; one started elsewhere is left running.
/// </summary>
internal sealed partial class EngineConnection(IEngineClient client, IEngineLauncher launcher, TimeSpan? startTimeout = null) : ObservableObject, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly TimeSpan _startTimeout = startTimeout ?? TimeSpan.FromSeconds(20);
    private IEngineProcess? _process;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsUnavailable), nameof(Description))]
    private EngineState _state = EngineState.Connecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    private EngineStatus? _status;

    /// <summary>Why the engine is unavailable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description))]
    private string? _problem;

    public bool IsConnected => State == EngineState.Connected;

    public bool IsUnavailable => State == EngineState.Unavailable;

    public string Description => State switch
    {
        EngineState.Connecting => "Connecting to the engine…",
        EngineState.Starting => "Starting the engine…",
        EngineState.Connected when Status is { } status => $"Engine {status.Version}, data in {status.DataFolder}",
        _ => Problem ?? "The engine isn't running.",
    };

    /// <summary>The app started the engine, so it stops it when it quits.</summary>
    public bool StartedHere => _process is { HasExited: false };

    /// <summary>Connects to the running engine, starting one when <paramref name="start"/> and none is listening.</summary>
    public async Task ConnectAsync(bool start, CancellationToken ct)
    {
        State = EngineState.Connecting;
        Problem = null;
        if (await TryStatusAsync(ct) is { } running)
        {
            Connected(running);
            return;
        }

        if (!start)
        {
            Unavailable("The engine isn't running.");
            return;
        }

        State = EngineState.Starting;
        _process?.Dispose();
        try
        {
            _process = launcher.Start();
        }
        catch (EngineLaunchException e)
        {
            Unavailable(e.Message);
            return;
        }

        for (var waited = TimeSpan.Zero; waited < _startTimeout; waited += PollInterval)
        {
            await Task.Delay(PollInterval, ct);
            if (await TryStatusAsync(ct) is { } started)
            {
                Connected(started);
                return;
            }

            if (_process.HasExited)
            {
                Unavailable($"The engine stopped right after starting; see {launcher.LogPath}.");
                return;
            }
        }

        Unavailable($"The engine didn't answer within {_startTimeout.TotalSeconds:0} seconds; see {launcher.LogPath}.");
    }

    /// <summary>The event stream broke: the engine stopped, or its API went away.</summary>
    public void Lost() => Unavailable("Lost the engine; it may have stopped.");

    /// <summary>Stops the engine if the app started it: asks it to shut down, and kills it if it doesn't.</summary>
    public async Task StopAsync()
    {
        if (_process is { HasExited: false } process)
        {
            await StopAsync(process);
        }
    }

    /// <summary>
    /// An engine is running from the app's folder: one the app started, or one left running by an earlier run. An
    /// update would replace its files, so it's applied only once that engine has stopped.
    /// </summary>
    public bool RunsFromApp()
    {
        if (StartedHere)
        {
            return true;
        }

        using var bundled = Status is { } status ? launcher.FindBundled(status.ProcessId) : null;
        return bundled is { HasExited: false };
    }

    /// <summary>
    /// Stops the engine before an update: one the app started, whatever keep running says, or one running from the
    /// app's folder. A review it was running is queued again, as when it's stopped with Ctrl+C.
    /// </summary>
    public async Task StopForUpdateAsync()
    {
        if (StartedHere)
        {
            await StopAsync(_process!);
            return;
        }

        using var bundled = Status is { } status ? launcher.FindBundled(status.ProcessId) : null;
        if (bundled is { HasExited: false })
        {
            await StopAsync(bundled);
        }
    }

    private async Task StopAsync(IEngineProcess process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await client.ShutdownAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception e) when (e is HttpRequestException or EngineApiException or OperationCanceledException or IOException)
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
    }

    public void Dispose() => _process?.Dispose();

    private async Task<EngineStatus?> TryStatusAsync(CancellationToken ct)
    {
        try
        {
            return await client.GetStatusAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or EngineApiException or IOException)
        {
            return null;
        }
    }

    private void Connected(EngineStatus status)
    {
        Status = status;
        Problem = null;
        State = EngineState.Connected;
    }

    private void Unavailable(string problem)
    {
        Problem = problem;
        State = EngineState.Unavailable;
    }
}
