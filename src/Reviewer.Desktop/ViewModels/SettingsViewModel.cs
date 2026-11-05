using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reviewer.Api;
using Reviewer.Cli;
using Reviewer.Desktop.Services;
using Reviewer.Engine.Config;

namespace Reviewer.Desktop.ViewModels;

/// <summary>An account, with whether its token works.</summary>
internal sealed partial class AccountItem(AccountConfig account, int? fileIndex) : ObservableObject
{
    public string Id { get; } = account.Id;

    public string Details { get; } = $"{account.Provider} · {account.Host.AbsoluteUri.TrimEnd('/')} · token: {account.Token ?? "the provider's defaults"}";

    /// <summary>Its index under <c>accounts</c> in the file; null for the implicit github.com account.</summary>
    public int? FileIndex { get; } = fileIndex;

    public bool CanRemove => FileIndex is not null;

    [ObservableProperty]
    private string _status = "checking…";

    [ObservableProperty]
    private bool _signedIn;
}

/// <summary>An agent's health, as <c>GET /agents</c> reports it.</summary>
internal sealed class AgentItem(AgentStatus agent)
{
    public string Id { get; } = agent.Id;

    public bool Ready { get; } = agent.Ready;

    public string Status { get; } = agent.Ready
        ? $"ready, {agent.Version}"
        : agent.Problem ?? (!agent.Installed ? "not installed" : !agent.LoggedIn ? "not logged in" : "not ready");
}

/// <summary>
/// Accounts and their tokens, the agents and their health, and the global policy and skills (<c>defaults</c>). Saving
/// edits only the keys that changed; a key unticked goes back to the built-in value.
/// </summary>
internal sealed partial class SettingsViewModel : PageViewModel
{
    public static IReadOnlyList<string> Providers { get; } = [AccountConfig.GitHub, AccountConfig.GitLab];

    private readonly IEngineClient _client;
    private readonly ConfigDocument _config;
    private readonly IKeychain _keychain;
    private int _statusRound;

    [ObservableProperty]
    private IReadOnlyList<SettingSection> _agentSettings = [];

    [ObservableProperty]
    private IReadOnlyList<SettingSection> _policy = [];

    [ObservableProperty]
    private IReadOnlyList<string> _problems = [];

    [ObservableProperty]
    private string _newAccountId = "";

    [ObservableProperty]
    private string _newProvider = AccountConfig.GitHub;

    [ObservableProperty]
    private string _newHost = "";

    [ObservableProperty]
    private string _newToken = "";

    public SettingsViewModel(IEngineClient client, ConfigDocument config, IKeychain keychain)
    {
        _client = client;
        _config = config;
        _keychain = keychain;
        _config.Changed += Rebuild;
    }

    public override string Title => "Settings";

    public override bool ReloadWhenShown => true;

    public ObservableCollection<AccountItem> Accounts { get; } = [];

    public ObservableCollection<AgentItem> Agents { get; } = [];

    public bool CanEdit => _config.Config is not null;

    /// <summary>The file has no accounts, so the implicit github.com one is in use; adding one writes it out too.</summary>
    public bool ImplicitAccount => CanEdit && Accounts.All(a => a.FileIndex is null);

    public override async Task LoadAsync()
    {
        Loading = true;
        await TryAsync(async () =>
        {
            await _config.LoadAsync();
            await LoadAgentsAsync();
        });
        Loading = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        List<ConfigEdit> edits;
        try
        {
            edits = [.. SettingSpecs.Edits(AgentSettings, "agents"), .. SettingSpecs.Edits(Policy, "defaults")];
        }
        catch (FormatException e)
        {
            Error = e.Message;
            return;
        }

        await TryAsync(() => _config.SaveAsync(edits));
    }

    [RelayCommand]
    private void Revert() => Rebuild();

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        if (_config.Config is not { } config)
        {
            return;
        }

        Uri? host = null;
        if (NewHost.Trim() is { Length: > 0 } text && !Uri.TryCreate(text, UriKind.Absolute, out host))
        {
            Error = $"'{text}' isn't a URL; give the host as https://gitlab.example.com.";
            return;
        }

        var id = NewAccountId.Trim() is { Length: > 0 } typed
            ? typed
            : AccountSetup.SuggestId(NewProvider, host ?? AccountSetup.DefaultHost(NewProvider), config.Accounts.Select(a => a.Id));
        if (config.Accounts.Any(a => a.Id == id))
        {
            Error = $"There's already an account '{id}'.";
            return;
        }

        // A pasted token goes to the keychain, and the file names it; a reference such as env:GITLAB_TOKEN goes in as it is.
        string? token;
        try
        {
            token = await AccountSetup.TokenReferenceAsync(_keychain, id, NewToken);
        }
        catch (KeychainException e)
        {
            Error = e.Message;
            return;
        }

        await TryAsync(async () =>
        {
            await _config.SaveAsync(AccountSetup.AddEdits(_config.Settings, config, AccountSetup.Entry(id, NewProvider, host, token)));
            (NewAccountId, NewHost, NewToken) = ("", "", "");
        });
    }

    /// <summary>Where the host field's hint points: the provider's own host, or what a self-hosted one looks like.</summary>
    public string HostPlaceholder => NewProvider == AccountConfig.GitLab
        ? "Host: empty for gitlab.com, or e.g. https://gitlab.example.com"
        : "Host: github.com";

    public string ScopeHint => AccountSetup.ScopeHint(NewProvider);

    /// <summary>Creates a token for the account being added, on its host, with the scope reviewing needs.</summary>
    public Uri TokenPage => AccountSetup.TokenPage(
        NewProvider,
        Uri.TryCreate(NewHost.Trim(), UriKind.Absolute, out var host) && host.Scheme == Uri.UriSchemeHttps ? host : AccountSetup.DefaultHost(NewProvider));

    partial void OnNewProviderChanged(string value)
    {
        OnPropertyChanged(nameof(HostPlaceholder));
        OnPropertyChanged(nameof(ScopeHint));
        OnPropertyChanged(nameof(TokenPage));
    }

    partial void OnNewHostChanged(string value) => OnPropertyChanged(nameof(TokenPage));

    [RelayCommand]
    private Task RemoveAccountAsync(AccountItem account) => account.FileIndex is { } index
        ? TryAsync(() => _config.SaveAsync([new ConfigEdit(ConfigEditOp.Remove, $"accounts[{index}]")]))
        : Task.CompletedTask;

    [RelayCommand]
    private Task CheckAgentsAsync() => TryAsync(LoadAgentsAsync);

    private async Task LoadAgentsAsync()
    {
        var agents = await _client.GetAgentsAsync(CancellationToken.None);
        Agents.Clear();
        foreach (var agent in agents)
        {
            Agents.Add(new AgentItem(agent));
        }
    }

    private void Rebuild()
    {
        Problems = _config.Problems;
        OnPropertyChanged(nameof(CanEdit));
        Accounts.Clear();
        if (_config.Config is not { } config)
        {
            AgentSettings = [];
            Policy = [];
            OnPropertyChanged(nameof(ImplicitAccount));
            return;
        }

        // With accounts in the file, the config's are those, in order; without, it's the implicit github.com one.
        var inFile = SettingField.At(_config.Settings, "accounts") is JsonArray { Count: > 0 };
        for (var i = 0; i < config.Accounts.Count; i++)
        {
            Accounts.Add(new AccountItem(config.Accounts[i], inFile ? i : null));
        }

        OnPropertyChanged(nameof(ImplicitAccount));
        _ = CheckAccountsAsync([.. Accounts]);

        var builtIn = ReviewerConfig.Default;
        AgentSettings = SettingSpecs.Build(SettingSpecs.Agents, SettingField.At(_config.Settings, "agents"), SettingSpecs.Json(builtIn.Agents));
        Policy = SettingSpecs.Build(SettingSpecs.Policy, SettingField.At(_config.Settings, "defaults"), SettingSpecs.Json(builtIn.Defaults));
    }

    /// <summary>Asks whether each token works; a later rebuild's answers replace these.</summary>
    private async Task CheckAccountsAsync(IReadOnlyList<AccountItem> accounts)
    {
        var round = ++_statusRound;
        foreach (var account in accounts)
        {
            string status;
            var signedIn = false;
            try
            {
                var answer = await _client.GetAccountStatusAsync(account.Id, CancellationToken.None);
                signedIn = answer.SignedIn;
                status = answer.SignedIn ? $"signed in as {answer.Login}" : answer.Problem ?? "not signed in";
            }
            catch (Exception e) when (e is EngineApiException or HttpRequestException or IOException)
            {
                status = e.Message;
            }

            if (round != _statusRound)
            {
                return;
            }

            (account.Status, account.SignedIn) = (status, signedIn);
        }
    }
}
