using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reviewer.Api;
using Reviewer.Cli;
using Reviewer.Desktop.Services;
using Reviewer.Engine.Config;

namespace Reviewer.Desktop.ViewModels;

/// <summary>An entry under <c>repos</c>, at its index in the file.</summary>
internal sealed class WatchedRepoItem(int index, RepoConfig repo, string account, string agent)
{
    public int Index { get; } = index;

    public string Repo { get; } = repo.Repo;

    public string Account { get; } = account;

    public string Details { get; } = $"{account} · {agent} · {SettingField.Text(SettingSpecs.Json(repo.Policy.Review.Mode))}";
}

/// <summary>A repo an account can see, to watch with one click.</summary>
internal sealed partial class RepoChoiceItem(string account, RepoChoice choice) : ObservableObject
{
    public string Account { get; } = account;

    public string Repo { get; } = choice.Repo;

    [ObservableProperty]
    private bool _watched = choice.Watched;
}

/// <summary>
/// The watched repos: add one by URL or by browsing an account, stop watching one, and set a repo's overrides. Every
/// change is an edit to <c>config.yaml</c> that keeps the rest of the file as it was. A URL on a host no account covers
/// opens <see cref="Connect"/>, which adds the account and then watches the repo.
/// </summary>
internal sealed partial class ReposViewModel : PageViewModel
{
    private readonly IEngineClient _client;
    private readonly ConfigDocument _config;
    private readonly IKeychain _keychain;

    /// <summary>The account to add for the pasted URL's host; null unless its host is new.</summary>
    [ObservableProperty]
    private ConnectAccountViewModel? _connect;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private WatchedRepoItem? _selected;

    /// <summary>The repo's agent, set under its entry rather than its overrides.</summary>
    [ObservableProperty]
    private SettingField? _agent;

    [ObservableProperty]
    private IReadOnlyList<SettingSection> _overrides = [];

    [ObservableProperty]
    private string _addUrl = "";

    [ObservableProperty]
    private string? _browseAccount;

    [ObservableProperty]
    private string _browseOwner = "";

    [ObservableProperty]
    private bool _browsing;

    [ObservableProperty]
    private IReadOnlyList<string> _problems = [];

    [ObservableProperty]
    private IReadOnlyList<string> _accounts = [];

    public ReposViewModel(IEngineClient client, ConfigDocument config, IKeychain keychain)
    {
        _client = client;
        _config = config;
        _keychain = keychain;
        _config.Changed += Rebuild;
    }

    public override string Title => "Repos";

    public override bool ReloadWhenShown => true;

    public ObservableCollection<WatchedRepoItem> Watched { get; } = [];

    public ObservableCollection<RepoChoiceItem> Choices { get; } = [];

    public bool HasSelection => Selected is not null;

    public bool CanEdit => _config.Config is not null;

    public override async Task LoadAsync()
    {
        Loading = true;
        await TryAsync(_config.LoadAsync);
        Loading = false;
    }

    partial void OnSelectedChanged(WatchedRepoItem? value) => BuildOverrides();

    [RelayCommand]
    private Task AddAsync()
    {
        if (_config.Config is not { } config)
        {
            return Task.CompletedTask;
        }

        Connect = null;
        if (RepoUrls.UnknownHost(AddUrl, config.Accounts) is { } host)
        {
            Error = null;
            Connect = new ConnectAccountViewModel(host, config.Accounts.Select(a => a.Id), ConnectAsync, () => Connect = null);
            return Task.CompletedTask;
        }

        if (RepoUrls.TryParse(AddUrl, config.Accounts, out var account, out var repo) is { } problem)
        {
            Error = problem;
            return Task.CompletedTask;
        }

        return WatchAsync(account.Id, repo, () => AddUrl = "");
    }

    /// <summary>
    /// Saves the pasted token and the account, checks the account signs in, then watches the pasted repo. A token that
    /// doesn't work leaves the account saved and the form open, to paste another.
    /// </summary>
    private async Task ConnectAsync(ConnectAccountViewModel connect)
    {
        if (_config.Config is not { } config)
        {
            return;
        }

        var id = connect.Id.Trim();
        if (id.Length == 0)
        {
            connect.Problem = "The account needs an id, such as gitlab.";
            return;
        }

        if (!Uri.TryCreate(connect.Host.Trim(), UriKind.Absolute, out var host))
        {
            connect.Problem = $"'{connect.Host}' isn't a URL; give it as https://gitlab.example.com.";
            return;
        }

        var existing = config.Accounts.Select((a, i) => (Account: a, Index: i)).FirstOrDefault(e => e.Account.Id == id);
        if (existing.Account is { } other && !ReviewerConfig.SameHost(other.Host, host))
        {
            connect.Problem = $"'{id}' is already the account for {other.Host.Authority}; choose another id.";
            return;
        }

        connect.Busy = true;
        connect.Problem = null;
        try
        {
            var token = await AccountSetup.TokenReferenceAsync(_keychain, id, connect.Token);
            connect.Token = "";

            // Connecting again after a token that didn't work: the account is there, so only its token may change.
            var edits = existing.Account is null
                ? AccountSetup.AddEdits(_config.Settings, config, AccountSetup.Entry(id, connect.Provider, host, token))
                : token is not null && token != existing.Account.Token
                    ? [new ConfigEdit(ConfigEditOp.Set, $"accounts[{existing.Index}].token", JsonValue.Create(token))]
                    : [];
            await _config.SaveAsync(edits);

            var status = await _client.GetAccountStatusAsync(id, CancellationToken.None);
            if (!status.SignedIn)
            {
                connect.Problem = $"Account '{id}' is saved, but it can't sign in: {(status.Problem ?? "not signed in").TrimEnd('.')}. Paste a token and connect again.";
                return;
            }

            Connect = null;
            if (RepoUrls.TryParse(AddUrl, _config.Config!.Accounts, out var account, out var repo) is { } problem)
            {
                // The host was given with a path the URL isn't under, or the URL names no repo.
                Error = $"Connected as {status.Login}, but: {problem}";
                return;
            }

            await WatchAsync(account.Id, repo, () => AddUrl = "");
        }
        catch (KeychainException e)
        {
            connect.Problem = e.Message;
        }
        catch (Exception e) when (e is EngineApiException or HttpRequestException or IOException)
        {
            connect.Problem = e is EngineApiException ? e.Message : $"Couldn't reach the engine: {e.Message}";
        }
        finally
        {
            connect.Busy = false;
        }
    }

    [RelayCommand]
    private Task WatchChoiceAsync(RepoChoiceItem choice) => WatchAsync(choice.Account, choice.Repo, () => choice.Watched = true);

    [RelayCommand]
    private Task RemoveAsync(WatchedRepoItem repo) =>
        TryAsync(() => _config.SaveAsync([new ConfigEdit(ConfigEditOp.Remove, $"repos[{repo.Index}]")]));

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (BrowseAccount is not { } account)
        {
            return;
        }

        Browsing = true;
        Choices.Clear();
        await TryAsync(async () =>
        {
            var owner = BrowseOwner.Trim();
            foreach (var choice in await _client.ListAccountReposAsync(account, owner.Length == 0 ? null : owner, CancellationToken.None))
            {
                Choices.Add(new RepoChoiceItem(account, choice));
            }
        });
        Browsing = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Selected is not { } repo || Agent is null)
        {
            return;
        }

        List<ConfigEdit> edits;
        try
        {
            edits = [.. new[] { Agent.ToEdit($"repos[{repo.Index}]") }.OfType<ConfigEdit>(), .. SettingSpecs.Edits(Overrides, $"repos[{repo.Index}].overrides")];
        }
        catch (FormatException e)
        {
            Error = e.Message;
            return;
        }

        await TryAsync(() => _config.SaveAsync(edits));
    }

    [RelayCommand]
    private void Revert() => BuildOverrides();

    private async Task WatchAsync(string account, string repo, Action done)
    {
        var config = _config.Config!;
        var entry = config.Repos.Select((r, i) => (r, i)).FirstOrDefault(e =>
            string.Equals(e.r.Repo, repo, StringComparison.OrdinalIgnoreCase) && (e.r.Account ?? config.Accounts[0].Id) == account);
        if (entry.r is not null)
        {
            Error = $"{repo} is already watched.";
            return;
        }

        // The account can be left out only when there's one, as people write it by hand.
        var value = new JsonObject { ["repo"] = repo };
        if (config.Accounts.Count > 1)
        {
            value["account"] = account;
        }

        await TryAsync(async () =>
        {
            await _config.SaveAsync([new ConfigEdit(ConfigEditOp.Add, "repos", value)]);
            done();
        });
    }

    /// <summary>The file was loaded or saved: list its repos again and rebuild the selected one's form.</summary>
    private void Rebuild()
    {
        Problems = _config.Problems;
        OnPropertyChanged(nameof(CanEdit));
        var selected = Selected?.Index;
        Watched.Clear();
        if (_config.Config is not { } config)
        {
            Accounts = [];
            Selected = null;
            return;
        }

        Accounts = [.. config.Accounts.Select(a => a.Id)];
        if (BrowseAccount is null || !Accounts.Contains(BrowseAccount))
        {
            BrowseAccount = Accounts[0];
        }

        for (var i = 0; i < config.Repos.Count; i++)
        {
            var repo = config.Repos[i];
            Watched.Add(new WatchedRepoItem(i, repo, repo.Account ?? config.Accounts[0].Id, repo.Agent ?? config.Agents.Default));
        }

        // Same index, same entry, unless it was the one removed.
        Selected = selected is { } at && at < Watched.Count ? Watched[at] : null;
        BuildOverrides();
    }

    private void BuildOverrides()
    {
        if (Selected is not { } repo || _config.Config is not { } config)
        {
            Agent = null;
            Overrides = [];
            return;
        }

        var entry = SettingField.At(_config.Settings, "repos") is JsonArray list && repo.Index < list.Count ? list[repo.Index] : null;
        Agent = new SettingField(
            new FieldSpec("agent", "Agent", FieldKind.Choice, AgentsConfig.Known), SettingField.At(entry, "agent"), JsonValue.Create(config.Agents.Default));
        Overrides = SettingSpecs.Build(SettingSpecs.Policy, SettingField.At(entry, "overrides"), SettingSpecs.Json(config.Defaults));
    }
}
