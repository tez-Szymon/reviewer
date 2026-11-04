using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reviewer.Api;
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

    public SettingsViewModel(IEngineClient client, ConfigDocument config)
    {
        _client = client;
        _config = config;
        _config.Changed += Rebuild;
    }

    public override string Title => "Settings";

    public override bool ReloadWhenShown => true;

    public ObservableCollection<AccountItem> Accounts { get; } = [];

    public ObservableCollection<AgentItem> Agents { get; } = [];

    public bool CanEdit => _config.Config is not null;

    /// <summary>The file has no accounts, so the implicit github.com one is in use; adding one replaces it.</summary>
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
    private Task AddAccountAsync()
    {
        var id = NewAccountId.Trim();
        if (id.Length == 0)
        {
            Error = "The account needs an id, such as gh-main.";
            return Task.CompletedTask;
        }

        var account = new JsonObject { ["id"] = id, ["provider"] = NewProvider };
        if (NewHost.Trim() is { Length: > 0 } host)
        {
            account["host"] = host;
        }

        // A reference such as env:GITHUB_TOKEN or keychain:reviewer/gh-main; the token itself never goes in the file.
        if (NewToken.Trim() is { Length: > 0 } token)
        {
            account["token"] = token;
        }

        return TryAsync(async () =>
        {
            await _config.SaveAsync([new ConfigEdit(ConfigEditOp.Add, "accounts", account)]);
            (NewAccountId, NewHost, NewToken) = ("", "", "");
        });
    }

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
