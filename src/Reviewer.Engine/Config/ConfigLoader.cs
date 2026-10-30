using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Reviewer.Core.Findings;
using Reviewer.Core.Policy;
using Reviewer.Core.Providers;
using Reviewer.Instructions;

namespace Reviewer.Engine.Config;

/// <summary>
/// Loads <c>config.yaml</c>: built-in defaults first, the user's file merged on top, then each repo's
/// <c>overrides</c> merged onto the result's <c>defaults</c>. Maps merge key by key; lists and values replace.
/// Unknown keys, wrong types and out-of-range values are errors, reported with the key and line.
/// </summary>
public static partial class ConfigLoader
{
    public const string FileName = "config.yaml";

    /// <summary>Keys are camelCase, enum values kebab-case (<c>gate-merge</c>, <c>assigned-to-me</c>).</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
        PropertyNameCaseInsensitive = false, // A misspelt "dryrun" should be an error, not quietly accepted.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Uri GitHubHost = new("https://github.com");

    /// <summary>Loads <paramref name="path"/>; a missing file means the built-in defaults.</summary>
    /// <exception cref="ConfigException">The file is invalid.</exception>
    public static async Task<ReviewerConfig> LoadAsync(string path, CancellationToken ct)
    {
        string yaml;
        try
        {
            yaml = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            yaml = "";
        }

        return Parse(yaml, path);
    }

    /// <param name="source">Names the file in error messages.</param>
    /// <exception cref="ConfigException">The config is invalid.</exception>
    public static ReviewerConfig Parse(string yaml, string source = FileName)
    {
        var user = YamlToJson.Convert(yaml, source);
        var errors = new Errors(source, user.Lines);

        var merged = Merge(BuiltIn(), user.Root);
        if (Bind<ConfigFile>(merged, "", errors) is not { } file)
        {
            throw new ConfigException(errors.List);
        }

        var accounts = file.Accounts.Count > 0
            ? file.Accounts.Select(a => new AccountConfig(a.Id, a.Provider, a.Host ?? GitHubHost, a.Token)).ToList()
            : [new AccountConfig("github", "github", GitHubHost, null)];
        ValidateAccounts(accounts, errors);
        ValidateAgents(file.Agents, errors);

        var defaultErrors = ValidatePolicy(file.Defaults);
        errors.AddAll("defaults", defaultErrors);

        var repos = new List<RepoConfig>(file.Repos.Count);
        for (var i = 0; i < file.Repos.Count; i++)
        {
            var entry = file.Repos[i];
            var path = $"repos[{i}]";
            ValidateRepo(entry, path, accounts, file.Repos.Take(i), errors);

            var policy = file.Defaults;
            if (entry.Overrides is not null)
            {
                var node = Merge((JsonObject)merged["defaults"]!.DeepClone(), (JsonObject)entry.Overrides.DeepClone());
                policy = Bind<PolicyConfig>(node, $"{path}.overrides", errors);
                if (policy is null)
                {
                    continue;
                }

                // A bad default shows up in every repo's policy too; report it once, under defaults.
                errors.AddAll($"{path}.overrides", ValidatePolicy(policy).Except(defaultErrors));
            }

            repos.Add(new RepoConfig(entry.Repo, entry.Account, entry.Agent, policy));
        }

        return errors.List.Count > 0
            ? throw new ConfigException(errors.List)
            : new ReviewerConfig(accounts, file.Agents, file.Defaults, repos);
    }

    /// <summary>A short, stable hash of the settings that shape a review's result.</summary>
    public static string Hash(string agentId, AgentSettings agent, PolicyConfig policy)
    {
        var input = new HashInput(agentId, agent, policy.Review, policy.Merge, policy.Instructions);
        var bytes = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input, JsonOptions));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    /// <summary>Copies <paramref name="overlay"/> onto <paramref name="target"/>: maps merge, everything else replaces.</summary>
    internal static JsonObject Merge(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay.ToList())
        {
            overlay.Remove(key);
            if (value is JsonObject map && target[key] is JsonObject existing)
            {
                Merge(existing, map);
            }
            else
            {
                target[key] = value;
            }
        }

        return target;
    }

    private static JsonObject BuiltIn()
    {
        using var stream = typeof(ConfigLoader).Assembly.GetManifestResourceStream("Reviewer.Engine.defaults.yaml")
            ?? throw new InvalidOperationException("Embedded default config is missing.");
        using var reader = new StreamReader(stream);
        return YamlToJson.Convert(reader.ReadToEnd(), "built-in defaults").Root;
    }

    private static T? Bind<T>(JsonObject node, string prefix, Errors errors)
        where T : class
    {
        try
        {
            return node.Deserialize<T>(JsonOptions) ?? throw new JsonException("Value is null.");
        }
        catch (JsonException e)
        {
            var (relative, message) = Describe<T>(e);
            errors.Add(Join(prefix, relative), message);
            return null;
        }
    }

    // System.Text.Json's messages name .NET types; say what's wrong in the config's own terms.
    private static (string Path, string Message) Describe<T>(JsonException e)
    {
        var path = PathSegment().Replace(e.Path ?? "$", ".$1").TrimStart('$').TrimStart('.');
        if (e.Message.Contains("could not be mapped to any .NET member", StringComparison.Ordinal))
        {
            return (path, "unknown key.");
        }

        if (MissingKeys().Match(e.Message) is { Success: true } missing)
        {
            return (path, $"missing {missing.Groups[1].Value}.");
        }

        if (e.Message.Contains("doesn't allow null", StringComparison.Ordinal))
        {
            return (path, "a value is required.");
        }

        // The message names the type that holds the value, so find the value's own type from the path.
        var type = TypeAt(typeof(T), path);
        type = Nullable.GetUnderlyingType(type ?? typeof(object)) ?? type;
        if (type is { IsEnum: true })
        {
            var names = Enum.GetNames(type).Select(JsonNamingPolicy.KebabCaseLower.ConvertName);
            return (path, $"must be one of: {string.Join(", ", names)}.");
        }

        return (path, "expected " + (type switch
        {
            null => "a different kind of value",
            _ when type == typeof(string) => "text",
            _ when type == typeof(bool) => "true or false",
            _ when type == typeof(int) => "a whole number",
            _ when type == typeof(double) => "a number",
            _ when type == typeof(Uri) => "a URL",
            _ when type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) => "a list",
            _ => "a map of settings",
        }) + ".");
    }

    private static Type? TypeAt(Type type, string path)
    {
        foreach (var segment in PathParts().Matches(path).Select(m => m.Value))
        {
            if (segment.StartsWith('['))
            {
                type = type.IsGenericType ? type.GetGenericArguments()[0] : typeof(object);
                continue;
            }

            var property = type.GetProperties().FirstOrDefault(p =>
                (p.GetCustomAttributes(typeof(JsonPropertyNameAttribute), true).FirstOrDefault() is JsonPropertyNameAttribute a
                    ? a.Name
                    : JsonOptions.PropertyNamingPolicy!.ConvertName(p.Name)) == segment);
            if (property is null)
            {
                return null;
            }

            type = property.PropertyType;
        }

        return type;
    }

    private static void ValidateAccounts(List<AccountConfig> accounts, Errors errors)
    {
        for (var i = 0; i < accounts.Count; i++)
        {
            var account = accounts[i];
            var path = $"accounts[{i}]";
            if (string.IsNullOrWhiteSpace(account.Id))
            {
                errors.Add($"{path}.id", "a value is required.");
            }
            else if (accounts.Take(i).Any(a => a.Id == account.Id))
            {
                errors.Add($"{path}.id", $"'{account.Id}' is already used by another account.");
            }

            switch (account.Provider)
            {
                // Compared the way ReviewerConfig.For matches PR URLs, so http:// or another port can't pass here and then match nothing.
                case "github" when !account.Host.IsAbsoluteUri || !ReviewerConfig.SameHost(account.Host, GitHubHost):
                    errors.Add($"{path}.host", "only https://github.com is supported for now; GitHub Enterprise Server comes later.");
                    break;
                case "github":
                    break;
                case "gitlab":
                    errors.Add($"{path}.provider", "GitLab support arrives in M3.");
                    break;
                default:
                    errors.Add($"{path}.provider", "must be github or gitlab.");
                    break;
            }

            // Never echo the value: if it's a pasted token, the error message must not spread it further.
            if (account.Token is { } token && !EnvReference().IsMatch(token))
            {
                errors.Add($"{path}.token", token.StartsWith("keychain:", StringComparison.Ordinal)
                    ? "keychain references arrive in M3; use env:NAME for now."
                    : "must be a reference such as env:GITHUB_TOKEN, never the token itself.");
            }
        }
    }

    private static void ValidateAgents(AgentsConfig agents, Errors errors)
    {
        if (!AgentsConfig.Known.Contains(agents.Default))
        {
            errors.Add("agents.default", $"must be one of: {string.Join(", ", AgentsConfig.Known)}.");
        }

        foreach (var id in AgentsConfig.Known)
        {
            var settings = agents.Get(id);
            if (settings.MaxConcurrency < 1)
            {
                errors.Add($"agents.{id}.maxConcurrency", "must be at least 1.");
            }

            if (settings.TimeoutMinutes is < 1 or > 24 * 60)
            {
                errors.Add($"agents.{id}.timeoutMinutes", "must be between 1 and 1440.");
            }
        }
    }

    private static void ValidateRepo(RepoEntry entry, string path, List<AccountConfig> accounts, IEnumerable<RepoEntry> earlier, Errors errors)
    {
        if (!RepoName().IsMatch(entry.Repo))
        {
            errors.Add($"{path}.repo", "must be owner/name.");
        }

        if (entry.Account is null && accounts.Count > 1)
        {
            errors.Add(path, "set account: more than one account is configured.");
        }
        else if (entry.Account is not null && accounts.All(a => a.Id != entry.Account))
        {
            errors.Add($"{path}.account", $"no account with id '{entry.Account}'.");
        }

        if (entry.Agent is not null && !AgentsConfig.Known.Contains(entry.Agent))
        {
            errors.Add($"{path}.agent", $"must be one of: {string.Join(", ", AgentsConfig.Known)}.");
        }

        // Compare the hosts the entries' accounts point at: a PR URL names a host, not an account, so two entries for
        // one repo on one host can't both be found, whichever accounts they name.
        Uri? Host(RepoEntry r) =>
            (r.Account is null ? (accounts.Count == 1 ? accounts[0] : null) : accounts.FirstOrDefault(a => a.Id == r.Account))?.Host;
        if (Host(entry) is { } host
            && earlier.Any(r => Host(r) is { } other && ReviewerConfig.SameHost(other, host)
                && string.Equals(r.Repo, entry.Repo, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"{path}.repo", $"{entry.Repo} is listed twice.");
        }
    }

    /// <summary>Errors as (path relative to the policy, message), so a repo's can be compared with the defaults'.</summary>
    private static List<(string Path, string Message)> ValidatePolicy(PolicyConfig policy)
    {
        var errors = new List<(string, string)>();
        var triggers = policy.Triggers;
        if (triggers.Initial.Contains(InitialTrigger.HasLabel) && triggers.Labels.Count == 0)
        {
            errors.Add(("triggers.labels", "has-label needs at least one label."));
        }

        if (triggers.Initial.Contains(InitialTrigger.AuthoredBy) && triggers.Authors.Count == 0)
        {
            errors.Add(("triggers.authors", "authored-by needs at least one author."));
        }

        if (triggers.ReReview.DebounceMinutes < 0)
        {
            errors.Add(("triggers.reReview.debounceMinutes", "must not be negative."));
        }

        if (triggers.MaxDiffLines < 1)
        {
            errors.Add(("triggers.maxDiffLines", "must be at least 1."));
        }

        var review = policy.Review;
        if (review.Mode == ReviewMode.GateMerge)
        {
            errors.Add(("review.mode", "gate-merge arrives in M3, with the merge guards."));
        }

        if (review.MinConfidence is < 0 or > 1)
        {
            errors.Add(("review.minConfidence", "must be between 0 and 1."));
        }

        for (var i = 0; i < review.BlockingRules.Count; i++)
        {
            if (review.BlockingRules[i].MinConfidence is < 0 or > 1)
            {
                errors.Add(($"review.blockingRules[{i}].minConfidence", "must be between 0 and 1."));
            }
        }

        if (review.MaxInlineComments < 0)
        {
            errors.Add(("review.maxInlineComments", "must not be negative."));
        }

        return errors;
    }

    private static string Join(string prefix, string path) =>
        prefix.Length == 0 ? path : path.Length == 0 ? prefix : path.StartsWith('[') ? prefix + path : $"{prefix}.{path}";

    [GeneratedRegex(@"\['([^']*)'\]")]
    private static partial Regex PathSegment();

    [GeneratedRegex(@"\[[0-9]+\]|[^.\[]+")]
    private static partial Regex PathParts();

    [GeneratedRegex(@"missing required properties including: (.*)\.")]
    private static partial Regex MissingKeys();

    [GeneratedRegex("^env:[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvReference();

    [GeneratedRegex(@"^[^/\s]+/[^/\s]+$")]
    private static partial Regex RepoName();

    /// <summary>Collects messages as <c>file:line: path: message</c>, with the line of the nearest key the user wrote.</summary>
    private sealed class Errors(string source, IReadOnlyDictionary<string, int> lines)
    {
        public List<string> List { get; } = [];

        public void Add(string path, string message)
        {
            var line = Line(path);
            List.Add(line is { } l ? $"{source}:{l}: {path}: {message}" : $"{source}: {path}: {message}");
        }

        public void AddAll(string prefix, IEnumerable<(string Path, string Message)> errors)
        {
            foreach (var (path, message) in errors)
            {
                Add(Join(prefix, path), message);
            }
        }

        private int? Line(string path)
        {
            while (path.Length > 0)
            {
                if (lines.TryGetValue(path, out var line))
                {
                    return line;
                }

                var cut = Math.Max(path.LastIndexOf('.'), path.LastIndexOf('['));
                path = cut > 0 ? path[..cut] : "";
            }

            return null;
        }
    }

    private sealed record ConfigFile(
        IReadOnlyList<AccountEntry> Accounts,
        AgentsConfig Agents,
        PolicyConfig Defaults,
        IReadOnlyList<RepoEntry> Repos);

    private sealed record AccountEntry(string Id, string Provider = "github", Uri? Host = null, string? Token = null);

    private sealed record RepoEntry(string Repo, string? Account = null, string? Agent = null, JsonObject? Overrides = null);

    private sealed record HashInput(string Agent, AgentSettings Settings, ReviewConfig Review, MergeConfig Merge, InstructionsConfig Instructions);
}
