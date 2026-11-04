using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using Reviewer.Engine.Config;

namespace Reviewer.Desktop.ViewModels;

public enum FieldKind
{
    /// <summary>One of <see cref="SettingField.Options"/>.</summary>
    Choice,

    /// <summary>True or false, chosen like a choice.</summary>
    Toggle,

    WholeNumber,

    Number,

    Text,

    /// <summary>A list of strings, one per line.</summary>
    Lines,
}

/// <summary>What a field is: its key path below the form's base, its label and its kind.</summary>
/// <param name="Path">Dotted keys, such as <c>review.mode</c> or <c>claude-code.model</c>.</param>
internal sealed record FieldSpec(string Path, string Label, FieldKind Kind, IReadOnlyList<string>? Options = null, string? Hint = null);

/// <summary>
/// One setting in a form. Either it's set at this level of <c>config.yaml</c>, or it takes the value it inherits: the
/// defaults' for a repo override, the built-in one for a global setting. Unticking a set field removes its key.
/// </summary>
internal sealed partial class SettingField : ObservableObject
{
    private readonly bool _wasSet;
    private readonly string _original;
    private readonly string _inherited;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private bool _isSet;

    /// <summary>The value as edited: an option for choices and toggles, text otherwise, lines for a list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private string _value;

    /// <param name="set">The value the file sets at this level, or null when it inherits.</param>
    /// <param name="inherited">The value it takes when not set here.</param>
    public SettingField(FieldSpec spec, JsonNode? set, JsonNode? inherited)
    {
        Spec = spec;
        _wasSet = set is not null;
        _inherited = Text(inherited);
        _original = _wasSet ? Text(set) : _inherited;
        _isSet = _wasSet;
        _value = _original;
    }

    public FieldSpec Spec { get; }

    public string Label => Spec.Label;

    public string? Hint => Spec.Hint;

    public bool IsChoice => Spec.Kind is FieldKind.Choice or FieldKind.Toggle;

    public bool IsLines => Spec.Kind == FieldKind.Lines;

    public bool IsText => !IsChoice;

    public IReadOnlyList<string> Options => Spec.Kind == FieldKind.Toggle ? ["true", "false"] : Spec.Options ?? [];

    /// <summary>What it inherits, for the line under the editor.</summary>
    public string InheritedText => _inherited.Length == 0
        ? "inherits: not set"
        : $"inherits: {(_inherited.Contains('\n', StringComparison.Ordinal) ? string.Join(", ", _inherited.Split('\n')) : _inherited)}";

    public bool IsChanged => IsSet != _wasSet || (IsSet && !string.Equals(Value, _original, StringComparison.Ordinal));

    /// <summary>The edit that saves this field under <paramref name="basePath"/>, or null when it's unchanged.</summary>
    /// <exception cref="FormatException">The value isn't of the field's kind.</exception>
    public ConfigEdit? ToEdit(string basePath)
    {
        var path = $"{basePath}.{Spec.Path}";
        if (!IsSet)
        {
            return _wasSet ? new ConfigEdit(ConfigEditOp.Remove, path) : null;
        }

        return IsChanged ? new ConfigEdit(ConfigEditOp.Set, path, Parse()) : null;
    }

    /// <summary>Ticked: starts from what it inherited. Unticked: shows what it inherits again.</summary>
    partial void OnIsSetChanged(bool value)
    {
        if (!value)
        {
            Value = _inherited;
        }
    }

    private JsonNode? Parse()
    {
        var text = (Value ?? "").Trim();
        switch (Spec.Kind)
        {
            case FieldKind.Toggle:
                return JsonValue.Create(text == "true");
            case FieldKind.WholeNumber:
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)
                    ? JsonValue.Create(whole)
                    : throw new FormatException($"{Label} needs a whole number.");
            case FieldKind.Number:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? JsonValue.Create(number)
                    : throw new FormatException($"{Label} needs a number, such as 0.6.");
            case FieldKind.Lines:
                var lines = new JsonArray();
                foreach (var line in (Value ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
                {
                    lines.Add(line);
                }

                return lines;
            default:
                // An emptied text field, such as a model, is null: the CLI's own default.
                return text.Length == 0 ? null : JsonValue.Create(text);
        }
    }

    /// <summary>A value as the editor shows it: scalars as YAML writes them, a list one item per line.</summary>
    internal static string Text(JsonNode? node) => node switch
    {
        null => "",
        JsonArray list => string.Join('\n', list.Select(Text)),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonValue value when value.GetValueKind() is JsonValueKind.True or JsonValueKind.False => value.GetValue<bool>() ? "true" : "false",
        _ => node.ToJsonString(),
    };

    /// <summary>The node at a dotted path below <paramref name="root"/>, or null when any key on the way is missing.</summary>
    internal static JsonNode? At(JsonNode? root, string path)
    {
        var node = root;
        foreach (var key in path.Split('.'))
        {
            node = node is JsonObject map && map.TryGetPropertyValue(key, out var child) ? child : null;
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }
}

/// <summary>Fields under one heading.</summary>
internal sealed record SettingSection(string Title, IReadOnlyList<SettingField> Fields);

/// <summary>The fields of a policy (<c>defaults</c>, or a repo's <c>overrides</c>) and of the agents.</summary>
internal static class SettingSpecs
{
    private static readonly IReadOnlyList<string> Modes = Names<Reviewer.Core.Policy.ReviewMode>();

    public static IReadOnlyList<(string Title, IReadOnlyList<FieldSpec> Fields)> Policy { get; } =
    [
        ("Review", [
            new("review.mode", "Mode", FieldKind.Choice, Modes, "comment posts findings; approve and gate also approve or block; gate-merge also merges"),
            new("review.dryRun", "Dry run", FieldKind.Toggle, Hint: "Review, but publish nothing"),
            new("review.minConfidence", "Minimum confidence", FieldKind.Number, Hint: "Findings below this, from 0 to 1, are dropped"),
            new("review.maxInlineComments", "Inline comments at most", FieldKind.WholeNumber),
            new("review.ignorePaths", "Ignore findings in", FieldKind.Lines, Hint: "One glob per line"),
            new("review.requestChangesFallback", "Without request changes", FieldKind.Choice, Names<Reviewer.Core.Policy.RequestChangesFallback>(),
                "What gate does where the platform can't request changes"),
        ]),
        ("Merge", [
            new("merge.method", "Method", FieldKind.Choice, Names<Reviewer.Core.Providers.MergeMethod>()),
            new("merge.requireGreenCi", "Require green CI", FieldKind.Toggle),
        ]),
        ("Triggers", [
            new("triggers.initial", "First review when", FieldKind.Lines, Hint: "One per line: " + string.Join(", ", Names<InitialTrigger>())),
            new("triggers.labels", "Labels", FieldKind.Lines, Hint: "For has-label"),
            new("triggers.authors", "Authors", FieldKind.Lines, Hint: "For authored-by"),
            new("triggers.skipDrafts", "Skip drafts", FieldKind.Toggle),
            new("triggers.targetBranches", "Target branches", FieldKind.Lines, Hint: "Empty means any"),
            new("triggers.paths", "Only PRs touching", FieldKind.Lines, Hint: "Globs; empty means any"),
            new("triggers.maxDiffLines", "Diff lines at most", FieldKind.WholeNumber),
            new("triggers.pollSeconds", "Poll every (seconds)", FieldKind.WholeNumber, Hint: $"At least {ConfigLoader.MinPollSeconds}"),
            new("triggers.reReview.onNewCommits", "Re-review on new commits", FieldKind.Toggle),
            new("triggers.reReview.debounceMinutes", "Wait after a push (minutes)", FieldKind.WholeNumber),
            new("triggers.reReview.onReassignment", "Re-review on reassignment", FieldKind.Toggle),
            new("triggers.reReview.onReviewRequested", "Re-review when requested", FieldKind.Toggle),
        ]),
        ("Instructions", [
            new("instructions.useDefaultGuide", "Use the built-in guide", FieldKind.Toggle),
            new("instructions.skills", "Skills", FieldKind.Lines, Hint: "Globs, one per line; relative to the data folder, ~ is home"),
            new("instructions.repoSkills", "Repo skills", FieldKind.Lines, Hint: "Globs, resolved the same way"),
            new("instructions.repoAgentFiles", "Repo agent files", FieldKind.Choice, Names<Reviewer.Instructions.RepoAgentFileMode>(),
                "CLAUDE.md, AGENTS.md and similar, from the base branch"),
        ]),
    ];

    /// <summary>The settings under <c>agents</c>.</summary>
    public static IReadOnlyList<(string Title, IReadOnlyList<FieldSpec> Fields)> Agents { get; } =
    [
        ("Agents", [
            new("default", "Default agent", FieldKind.Choice, AgentsConfig.Known),
        ]),
        .. AgentsConfig.Known.Select(id => (id, (IReadOnlyList<FieldSpec>)[
            new($"{id}.maxConcurrency", "Reviews at once", FieldKind.WholeNumber),
            new($"{id}.timeoutMinutes", "Timeout (minutes)", FieldKind.WholeNumber),
            new($"{id}.model", "Model", FieldKind.Text, Hint: "Empty uses the CLI's own default"),
        ])),
    ];

    /// <summary>Builds sections from <paramref name="specs"/>, reading set values below <paramref name="set"/>.</summary>
    public static IReadOnlyList<SettingSection> Build(
        IReadOnlyList<(string Title, IReadOnlyList<FieldSpec> Fields)> specs, JsonNode? set, JsonNode? inherited) =>
        [.. specs.Select(s => new SettingSection(s.Title, [.. s.Fields.Select(f => new SettingField(f, SettingField.At(set, f.Path), SettingField.At(inherited, f.Path)))]))];

    /// <summary>The edits that save <paramref name="sections"/> under <paramref name="basePath"/>.</summary>
    /// <exception cref="FormatException">A value isn't of its field's kind.</exception>
    public static List<ConfigEdit> Edits(IEnumerable<SettingSection> sections, string basePath) =>
        [.. sections.SelectMany(s => s.Fields).Select(f => f.ToEdit(basePath)).OfType<ConfigEdit>()];

    /// <summary>A value as config and API JSON write it.</summary>
    public static JsonNode? Json<T>(T value) => JsonSerializer.SerializeToNode(value, ConfigLoader.JsonOptions);

    private static IReadOnlyList<string> Names<T>() where T : struct, Enum =>
        [.. Enum.GetNames<T>().Select(JsonNamingPolicy.KebabCaseLower.ConvertName)];
}
