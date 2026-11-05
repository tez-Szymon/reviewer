using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reviewer.Agents.Cli;
using Reviewer.Api;
using Reviewer.Cli;
using Reviewer.Desktop.Services;

namespace Reviewer.Desktop.ViewModels;

/// <summary>
/// The window: the engine connection, the pages, and the job feed that keeps them current. While the engine is
/// away it's looked for every few seconds, and the feed resumes where it stopped.
/// </summary>
internal sealed partial class MainWindowViewModel : ObservableObject, IShell, IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);

    private readonly IEngineClient _client;
    private readonly JobFeed _feed;
    private readonly Func<Uri, Task> _openUri;
    private readonly Func<string, Task> _openFolder;
    private readonly Stack<PageViewModel> _back = new();
    private readonly DesktopSettings _settings;
    private readonly IAppUpdater _updater;
    private readonly ILoginItem? _loginItem;
    private readonly CancellationTokenSource _updates = new();
    private CancellationTokenSource _run = new();

    /// <summary>Quitting leaves an engine the app started running; saved in <c>desktop.json</c>.</summary>
    [ObservableProperty]
    private bool _keepEngineRunning;

    /// <summary>Why the app's preferences couldn't be saved.</summary>
    [ObservableProperty]
    private string? _preferencesProblem;

    /// <summary>Light, dark, or as the OS is; saved in <c>desktop.json</c>.</summary>
    [ObservableProperty]
    private Appearance _appearance;

    /// <summary>The app starts, in the background, when the user logs in.</summary>
    [ObservableProperty]
    private bool _startAtLogin;

    [ObservableProperty]
    private PageViewModel _current;

    [ObservableProperty]
    private PageViewModel _selectedPage;

    public MainWindowViewModel(
        IEngineClient client,
        EngineConnection engine,
        DesktopSettings settings,
        IAppUpdater updater,
        ILoginItem? loginItem,
        Action<Action> post,
        Func<Uri, Task> openUri,
        Func<string, Task> openFolder,
        IKeychain? keychain = null)
    {
        keychain ??= new OsKeychain(new CliWrapProcessRunner());
        _client = client;
        Engine = engine;
        _settings = settings;
        var preferences = settings.Load();
        _keepEngineRunning = preferences.KeepEngineRunning;
        _appearance = preferences.Appearance;
        _updater = updater;
        Updates = new UpdatesViewModel(updater);
        _loginItem = loginItem;
        _startAtLogin = CanStartAtLogin && loginItem!.IsEnabled;
        _openUri = openUri;
        _openFolder = openFolder;
        var config = new ConfigDocument(client);
        Queue = new QueueViewModel(client, this);
        Pages =
        [
            Queue, new PullsViewModel(client, this), new HistoryViewModel(client, this),
            new ReposViewModel(client, config, keychain), new SettingsViewModel(client, config, keychain),
        ];
        Queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueueViewModel.Activity))
            {
                OnPropertyChanged(nameof(TrayText));
            }
        };
        Engine.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EngineConnection.Description))
            {
                OnPropertyChanged(nameof(TrayText));
                OnPropertyChanged(nameof(EngineHeadline));
            }
        };
        _selectedPage = _current = Pages[0];

        _feed = new JobFeed(client, post);
        _feed.Opened += () => _ = LoadAllAsync();
        _feed.Changed += job =>
        {
            foreach (var page in Pages)
            {
                page.OnChanged(job);
            }

            if (Current is JobDetailViewModel detail)
            {
                detail.OnChanged(job);
            }
        };
        _feed.Lost += () =>
        {
            Engine.Lost();
            _ = RunAsync(start: false);
        };
    }

    public EngineConnection Engine { get; }

    public IReadOnlyList<PageViewModel> Pages { get; }

    public QueueViewModel Queue { get; }

    public UpdatesViewModel Updates { get; }

    public static IReadOnlyList<Appearance> Appearances { get; } = Enum.GetValues<Appearance>();

    /// <summary>
    /// Start at login is offered on macOS and Windows, to an installed app: a development build would register the
    /// build folder.
    /// </summary>
    public bool CanStartAtLogin => _loginItem is not null && _updater.IsInstalled;

    /// <summary>The engine's state in two or three words, as the sidebar shows it above what the queue holds.</summary>
    public string EngineHeadline => Engine.State switch
    {
        EngineState.Connected => "Engine running",
        EngineState.Starting => "Starting the engine…",
        EngineState.Connecting => "Connecting…",
        _ => "Engine stopped",
    };

    /// <summary>The tray icon's tooltip: what the engine is doing, or why there's none.</summary>
    public string TrayText => Engine.IsConnected ? $"Reviewer: {Queue.Activity}" : $"Reviewer: {Engine.Description}";

    /// <summary>Connects, starting the engine when <paramref name="start"/>, then follows its events until it goes away.</summary>
    public async Task RunAsync(bool start)
    {
        await _run.CancelAsync();
        _run.Dispose();
        _run = new CancellationTokenSource();
        var ct = _run.Token;
        try
        {
            await Engine.ConnectAsync(start, ct);
            while (Engine.State != EngineState.Connected)
            {
                await Task.Delay(RetryInterval, ct);
                await Engine.ConnectAsync(start: false, ct);
            }

            _ = _feed.RunAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    [RelayCommand]
    private Task StartEngineAsync() => RunAsync(start: true);

    /// <summary>Looks for updates in the background, from now until the app quits.</summary>
    public void StartUpdateChecks() => _ = Updates.RunAsync(_updates.Token);

    /// <summary>
    /// Stops the engine if it runs from the app's folder, which the update replaces, even when it's kept running;
    /// then the app exits, the update is applied, and the new version starts, starting the engine again.
    /// </summary>
    [RelayCommand]
    private async Task RestartToUpdateAsync()
    {
        await _updates.CancelAsync();
        await _run.CancelAsync();
        await Engine.StopForUpdateAsync();
        _updater.ApplyAndRestart();
    }

    partial void OnSelectedPageChanged(PageViewModel value)
    {
        _back.Clear();
        Current = value;
        if (value.ReloadWhenShown && Engine.IsConnected)
        {
            _ = value.LoadAsync();
        }
    }

    public void OpenJob(string id)
    {
        _back.Push(Current);
        var detail = new JobDetailViewModel(_client, this, id);
        Current = detail;
        _ = detail.LoadAsync();
    }

    public void Back()
    {
        if (_back.TryPop(out var previous))
        {
            Current = previous;
        }
    }

    public Task OpenUriAsync(Uri uri) => _openUri(uri);

    public Task OpenFolderAsync(string path) => _openFolder(path);

    /// <summary>
    /// Stops following the engine, and stops the engine if the app started it, unless <see cref="KeepEngineRunning"/>:
    /// then it goes on reviewing, and the app finds it on the socket the next time it starts.
    /// </summary>
    /// <remarks>
    /// A downloaded update is applied once the app has exited, unless an engine left running uses the app's folder:
    /// then it waits for Restart to update.
    /// </remarks>
    public async Task ShutdownAsync()
    {
        await _updates.CancelAsync();
        await _run.CancelAsync();
        if (!KeepEngineRunning)
        {
            await Engine.StopAsync();
        }

        if (Updates.IsReady && !Engine.RunsFromApp())
        {
            _updater.ApplyOnExit();
        }
    }

    partial void OnKeepEngineRunningChanged(bool value) => SavePreferences();

    partial void OnAppearanceChanged(Appearance value) => SavePreferences();

    private void SavePreferences() =>
        PreferencesProblem = _settings.Save(new DesktopPreferences(KeepEngineRunning, Appearance));

    partial void OnStartAtLoginChanged(bool value)
    {
        if (_loginItem?.Set(value) is { } problem)
        {
            PreferencesProblem = problem;
            StartAtLogin = _loginItem.IsEnabled;
        }
        else
        {
            PreferencesProblem = null;
        }
    }

    public void Dispose()
    {
        _updates.Dispose();
        _run.Dispose();
        Engine.Dispose();
    }

    private async Task LoadAllAsync()
    {
        await Task.WhenAll(Pages.Select(p => p.LoadAsync()));
        if (Current is JobDetailViewModel detail)
        {
            await detail.LoadAsync();
        }
    }
}
