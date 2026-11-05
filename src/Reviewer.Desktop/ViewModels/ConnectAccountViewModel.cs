using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reviewer.Engine.Config;

namespace Reviewer.Desktop.ViewModels;

/// <summary>
/// The account for a pasted repo URL whose host no account has: filled in from the URL, so connecting is pasting a
/// token. The Repos page saves it, checks it signs in, and then watches the repo.
/// </summary>
internal sealed partial class ConnectAccountViewModel : ObservableObject
{
    public ConnectAccountViewModel(Uri host, IEnumerable<string> takenIds, Func<ConnectAccountViewModel, Task> connect, Action cancel)
    {
        Provider = AccountSetup.ProviderFor(host);
        HostName = host.Authority;
        _host = host.AbsoluteUri.TrimEnd('/');
        _id = AccountSetup.SuggestId(Provider, host, takenIds);
        ConnectCommand = new AsyncRelayCommand(() => connect(this));
        CancelCommand = new RelayCommand(cancel);
    }

    public string Provider { get; }

    /// <summary>The host as the URL named it, for the heading.</summary>
    public string HostName { get; }

    public string Title => $"Connect {HostName}";

    public string Lead => Provider == AccountConfig.GitLab
        ? "No account covers this GitLab yet. Paste a token to add one; it's saved in the OS keychain and config.yaml only names it."
        : "No account covers github.com. Paste a token to add one; it's saved in the OS keychain and config.yaml only names it.";

    public bool IsGitLab => Provider == AccountConfig.GitLab;

    /// <summary>The instance's URL; a self-hosted GitLab served from a path needs that path added.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenPage))]
    private string _host;

    [ObservableProperty]
    private string _id;

    [ObservableProperty]
    private string _token = "";

    /// <summary>Why the last try didn't connect; null when there's nothing to say.</summary>
    [ObservableProperty]
    private string? _problem;

    [ObservableProperty]
    private bool _busy;

    public string ScopeHint => AccountSetup.ScopeHint(Provider);

    public Uri TokenPage => AccountSetup.TokenPage(
        Provider, Uri.TryCreate(Host.Trim(), UriKind.Absolute, out var host) && host.Scheme == Uri.UriSchemeHttps ? host : AccountSetup.DefaultHost(Provider));

    public IAsyncRelayCommand ConnectCommand { get; }

    public IRelayCommand CancelCommand { get; }
}
