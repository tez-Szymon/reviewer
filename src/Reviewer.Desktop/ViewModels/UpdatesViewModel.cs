using CommunityToolkit.Mvvm.ComponentModel;
using Reviewer.Desktop.Services;

namespace Reviewer.Desktop.ViewModels;

/// <summary>
/// Looks for a newer release when the app starts and every few hours, and downloads it, so it's ready to apply:
/// at once with Restart to update, or when the app quits.
/// </summary>
internal sealed partial class UpdatesViewModel(IAppUpdater updater) : ObservableObject
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    /// <summary>A downloaded version, ready to apply.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(ReadyText))]
    private string? _readyVersion;

    /// <summary>Why the last check failed; the next one may work.</summary>
    [ObservableProperty]
    private string? _problem;

    public bool IsInstalled => updater.IsInstalled;

    public bool IsReady => ReadyVersion is not null;

    public string ReadyText => $"Reviewer {ReadyVersion} is ready";

    /// <summary>The app's version, as the status bar shows it.</summary>
    public string Version => updater.CurrentVersion is { } version ? $"Reviewer {version}" : "Reviewer (development build)";

    /// <summary>Checks now and every <see cref="CheckInterval"/>, until an update is ready or <paramref name="ct"/> ends.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (!IsInstalled)
        {
            return;
        }

        try
        {
            while (!IsReady)
            {
                await CheckAsync(ct);
                if (!IsReady)
                {
                    await Task.Delay(CheckInterval, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            ReadyVersion = await updater.DownloadLatestAsync(ct);
            Problem = null;
        }
#pragma warning disable CA1031 // The feed, the network or the download can fail in many ways; each is just reported.
        catch (Exception e) when (e is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Problem = $"Couldn't check for updates: {e.Message}";
        }
    }
}
