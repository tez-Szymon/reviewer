using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reviewer.Api;
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
    private CancellationTokenSource _run = new();

    /// <summary>Quitting leaves an engine the app started running; saved in <c>desktop.json</c>.</summary>
    [ObservableProperty]
    private bool _keepEngineRunning;

    /// <summary>Why the app's preferences couldn't be saved.</summary>
    [ObservableProperty]
    private string? _preferencesProblem;

    [ObservableProperty]
    private PageViewModel _current;

    [ObservableProperty]
    private PageViewModel _selectedPage;

    public MainWindowViewModel(
        IEngineClient client,
        EngineConnection engine,
        DesktopSettings settings,
        Action<Action> post,
        Func<Uri, Task> openUri,
        Func<string, Task> openFolder)
    {
        _client = client;
        Engine = engine;
        _settings = settings;
        _keepEngineRunning = settings.Load().KeepEngineRunning;
        _openUri = openUri;
        _openFolder = openFolder;
        var config = new ConfigDocument(client);
        Queue = new QueueViewModel(client, this);
        Pages =
        [
            Queue, new PullsViewModel(client, this), new HistoryViewModel(client, this),
            new ReposViewModel(client, config), new SettingsViewModel(client, config),
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
    public async Task ShutdownAsync()
    {
        await _run.CancelAsync();
        if (!KeepEngineRunning)
        {
            await Engine.StopAsync();
        }
    }

    partial void OnKeepEngineRunningChanged(bool value) => PreferencesProblem = _settings.Save(new DesktopPreferences(value));

    public void Dispose()
    {
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
