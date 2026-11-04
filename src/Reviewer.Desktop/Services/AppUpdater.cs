using Velopack;
using Velopack.Sources;

namespace Reviewer.Desktop.Services;

/// <summary>Finds, downloads and applies app updates. Only an installed app is updated; a development build isn't.</summary>
internal interface IAppUpdater
{
    /// <summary>The app was installed by its installer, so it can update itself.</summary>
    bool IsInstalled { get; }

    /// <summary>The installed version, or null for a development build.</summary>
    string? CurrentVersion { get; }

    /// <summary>Downloads the newest release if it's newer than this one.</summary>
    /// <returns>The version ready to apply, or null when this one is the newest.</returns>
    Task<string?> DownloadLatestAsync(CancellationToken ct);

    /// <summary>Exits the app at once, applies the downloaded update, and starts the new version.</summary>
    void ApplyAndRestart();

    /// <summary>Applies the downloaded update once the app has exited, without starting it again.</summary>
    void ApplyOnExit();
}

/// <summary>
/// Updates from the repository's GitHub releases, through Velopack. Each package's channel is its runtime
/// (<c>osx-arm64</c>, <c>win-x64</c>…), so the app only ever sees releases built for it. <c>REVIEWER_UPDATE_URL</c>,
/// a folder or URL holding a release feed, replaces GitHub, to try an update before publishing it.
/// </summary>
internal sealed class VelopackUpdater : IAppUpdater
{
    public const string Repository = "https://github.com/tez-Szymon/reviewer";

    private readonly UpdateManager _manager;
    private VelopackAsset? _ready;

    public VelopackUpdater()
    {
        _manager = Environment.GetEnvironmentVariable("REVIEWER_UPDATE_URL") is { Length: > 0 } feed
            ? new UpdateManager(feed)
            : new UpdateManager(new GithubSource(Repository, accessToken: null, prerelease: false));

        // Downloaded in an earlier run but not applied yet.
        _ready = _manager.IsInstalled ? _manager.UpdatePendingRestart : null;
    }

    public bool IsInstalled => _manager.IsInstalled;

    public string? CurrentVersion => _manager.CurrentVersion?.ToString();

    public async Task<string?> DownloadLatestAsync(CancellationToken ct)
    {
        if (!_manager.IsInstalled)
        {
            return null;
        }

        if (await _manager.CheckForUpdatesAsync() is not { } update)
        {
            return _ready?.Version.ToString();
        }

        await _manager.DownloadUpdatesAsync(update, progress: null, cancelToken: ct);
        _ready = update.TargetFullRelease;
        return _ready.Version.ToString();
    }

    public void ApplyAndRestart()
    {
        if (_ready is { } ready)
        {
            _manager.ApplyUpdatesAndRestart(ready);
        }
    }

    public void ApplyOnExit()
    {
        if (_ready is { } ready)
        {
            _manager.WaitExitThenApplyUpdates(ready, silent: true, restart: false);
        }
    }
}
