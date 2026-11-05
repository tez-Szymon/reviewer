using System.Text.Json.Nodes;
using Reviewer.Cli;
using Reviewer.Engine.Config;

namespace Reviewer.Desktop.ViewModels;

/// <summary>
/// Adding an account, as the Repos and Settings pages both do: a pasted token goes to the OS keychain, the entry under
/// <c>accounts</c> names it, and the repos already watched keep the account they had.
/// </summary>
internal static class AccountSetup
{
    /// <summary>The keychain service a pasted token is saved under; the item's account is the account's id.</summary>
    public const string KeychainService = "reviewer";

    private static readonly Uri GitHubHost = new("https://github.com");
    private static readonly Uri GitLabHost = new("https://gitlab.com");

    /// <summary>The host an account of <paramref name="provider"/> has when it names none.</summary>
    public static Uri DefaultHost(string provider) => provider == AccountConfig.GitLab ? GitLabHost : GitHubHost;

    /// <summary>The provider for a host no account has: GitHub for github.com, which is the only GitHub host supported, else GitLab.</summary>
    public static string ProviderFor(Uri host) => ReviewerConfig.SameHost(host, GitHubHost) ? AccountConfig.GitHub : AccountConfig.GitLab;

    /// <summary>An id for a new account on <paramref name="host"/>, such as <c>gitlab</c> or <c>git-example-com</c>, unused so far.</summary>
    public static string SuggestId(string provider, Uri host, IEnumerable<string> taken)
    {
        var name = ReviewerConfig.SameHost(host, DefaultHost(provider)) ? provider : host.Host.Replace('.', '-').ToLowerInvariant();
        var used = taken.ToHashSet(StringComparer.Ordinal);
        var id = name;
        for (var n = 2; used.Contains(id); n++)
        {
            id = $"{name}-{n}";
        }

        return id;
    }

    /// <summary>
    /// The page that creates a token with what reviewing needs already filled in: the <c>api</c> scope on GitLab, the
    /// <c>repo</c> scope on GitHub.
    /// </summary>
    public static Uri TokenPage(string provider, Uri host) => provider == AccountConfig.GitLab
        ? new Uri($"{host.AbsoluteUri.TrimEnd('/')}/-/user_settings/personal_access_tokens?name=Reviewer&scopes=api")
        : new Uri("https://github.com/settings/tokens/new?description=Reviewer&scopes=repo");

    /// <summary>What the new account's token needs, for the form's caption.</summary>
    public static string ScopeHint(string provider) => provider == AccountConfig.GitLab
        ? "GitLab tokens need the api scope. Left empty, GITLAB_TOKEN is used."
        : "GitHub tokens need the repo scope. Left empty, GITHUB_TOKEN, GH_TOKEN or gh auth login is used.";

    /// <summary>
    /// What goes under <c>token</c> for <paramref name="input"/>: a reference as it is, or a pasted token saved to the
    /// keychain first and named by its reference; null when it's empty.
    /// </summary>
    /// <exception cref="KeychainException">The token couldn't be saved.</exception>
    public static async Task<string?> TokenReferenceAsync(IKeychain keychain, string id, string input)
    {
        input = input.Trim();
        if (input.Length == 0)
        {
            return null;
        }

        if (TokenReference.TryParse(input, out _))
        {
            return input;
        }

        var item = new TokenReference.Keychain(KeychainService, id);
        await keychain.WriteAsync(item, input, CancellationToken.None);
        return TokenReference.KeychainPrefix + item.Target;
    }

    /// <summary>The entry under <c>accounts</c>; the host only when it isn't the provider's default.</summary>
    public static JsonObject Entry(string id, string provider, Uri? host, string? token)
    {
        var entry = new JsonObject { ["id"] = id, ["provider"] = provider };
        if (host is not null && !(ReviewerConfig.SameHost(host, DefaultHost(provider)) && host.AbsolutePath == "/"))
        {
            entry["host"] = host.AbsoluteUri.TrimEnd('/');
        }

        if (token is not null)
        {
            entry["token"] = token;
        }

        return entry;
    }

    /// <summary>
    /// The edits that add <paramref name="entry"/> without moving the repos already watched. With no accounts in the
    /// file, github.com is used implicitly; an account on another host would replace it, so it's written out as the
    /// <c>github</c> account first. And a repo that names no account, as is allowed while there's one, is given that
    /// one, since with two it must say which.
    /// </summary>
    /// <param name="settings">What the file itself sets.</param>
    /// <param name="config">The file, merged.</param>
    public static List<ConfigEdit> AddEdits(JsonObject settings, ReviewerConfig config, JsonObject entry)
    {
        var edits = new List<ConfigEdit>();
        var host = entry["host"] is JsonValue h && Uri.TryCreate(h.GetValue<string>(), UriKind.Absolute, out var uri)
            ? uri
            : DefaultHost(entry["provider"]?.GetValue<string>() ?? AccountConfig.GitHub);
        var implicitAccount = SettingField.At(settings, "accounts") is not JsonArray { Count: > 0 };
        var replacesImplicit = implicitAccount && ReviewerConfig.SameHost(host, GitHubHost);
        if (implicitAccount && !replacesImplicit)
        {
            var github = config.Accounts[0];
            edits.Add(new ConfigEdit(ConfigEditOp.Add, "accounts", new JsonObject { ["id"] = github.Id, ["provider"] = github.Provider }));
        }

        if (config.Accounts.Count == 1 && !replacesImplicit && SettingField.At(settings, "repos") is JsonArray repos)
        {
            for (var i = 0; i < repos.Count; i++)
            {
                if (repos[i] is JsonObject repo && repo["account"] is null)
                {
                    edits.Add(new ConfigEdit(ConfigEditOp.Set, $"repos[{i}].account", JsonValue.Create(config.Accounts[0].Id)));
                }
            }
        }

        edits.Add(new ConfigEdit(ConfigEditOp.Add, "accounts", entry));
        return edits;
    }
}
