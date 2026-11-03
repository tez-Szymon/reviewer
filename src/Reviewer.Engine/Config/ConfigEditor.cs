using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Reviewer.Engine.Config;

public enum ConfigEditOp
{
    /// <summary>Set the value at the path, creating the maps on the way.</summary>
    Set,

    /// <summary>Remove the key or list item at the path.</summary>
    Remove,

    /// <summary>Append the value to the list at the path, creating the list if there's none.</summary>
    Add,
}

/// <summary>One change to <c>config.yaml</c>.</summary>
/// <param name="Path">A key path as config errors print it: <c>defaults.review.mode</c>, <c>repos[2].overrides</c>.</param>
public sealed record ConfigEdit(ConfigEditOp Op, string Path, JsonNode? Value = null);

/// <summary>
/// Edits <c>config.yaml</c> as text, so a file people also edit by hand keeps its comments and layout. Each edit
/// rewrites only the span of the node it changes: a new value is written in flow style (<c>{ mode: gate }</c>), a new
/// key goes on its own line after its siblings, and a removed key or list item takes its lines with it.
/// </summary>
/// <remarks>
/// It edits the text, nothing more: whether the result is a valid config is for <see cref="ConfigLoader.Parse"/> to say.
/// </remarks>
public static partial class ConfigEditor
{
    /// <summary>Applies <paramref name="edits"/> in order.</summary>
    /// <exception cref="ConfigException">The file isn't YAML, or an edit's path doesn't fit it.</exception>
    public static string Apply(string yaml, IEnumerable<ConfigEdit> edits)
    {
        foreach (var edit in edits)
        {
            var path = ParsePath(edit.Path);
            yaml = edit.Op switch
            {
                ConfigEditOp.Set => Set(yaml, path, edit.Value),
                ConfigEditOp.Remove => Remove(yaml, path),
                ConfigEditOp.Add => Add(yaml, path, edit.Value),
                _ => throw new ArgumentOutOfRangeException(nameof(edits), edit.Op, "Unknown edit."),
            };
        }

        return yaml;
    }

    private static string Set(string yaml, IReadOnlyList<object> path, JsonNode? value)
    {
        var doc = Document.Load(yaml);
        var found = doc.Find(path);
        if (found.Missing == 0)
        {
            return doc.ReplaceValue(found, Flow(value));
        }

        // Build what's missing below the deepest node that exists, and add it there as one key.
        if (path[^found.Missing] is not string key)
        {
            throw Error($"{Display(path)}: {Display(path.Take(path.Count - found.Missing + 1))} doesn't exist.");
        }

        var nested = value?.DeepClone();
        foreach (var segment in path.Skip(path.Count - found.Missing + 1).Reverse())
        {
            nested = segment is string name ? new JsonObject { [name] = nested } : throw Error($"{Display(path)}: no list to put item {segment} in.");
        }

        return found.Node switch
        {
            null => doc.AppendToRoot(key, nested),
            YamlMappingNode map => doc.InsertKey(map, key, nested),
            _ when Document.IsEmpty(found.Node) => doc.ReplaceValue(found, Flow(new JsonObject { [key] = nested })),
            _ => throw Error($"{Display(path)}: {Display(path.Take(path.Count - found.Missing))} isn't a map."),
        };
    }

    private static string Remove(string yaml, IReadOnlyList<object> path)
    {
        if (path.Count == 0)
        {
            throw Error("remove needs a path.");
        }

        var doc = Document.Load(yaml);
        var found = doc.Find(path);
        if (found.Missing > 0)
        {
            throw Error($"{Display(path)} doesn't exist.");
        }

        // An empty block map or list would read as null, so the last entry leaves {} or [] behind.
        var parent = path.Take(path.Count - 1).ToList();
        return found.Parent switch
        {
            YamlMappingNode { Children.Count: 1 } map when parent.Count > 0 || map.Style == MappingStyle.Flow => Set(yaml, parent, new JsonObject()),
            YamlSequenceNode { Children.Count: 1 } => Set(yaml, parent, new JsonArray()),
            _ => doc.RemoveEntry(found),
        };
    }

    private static string Add(string yaml, IReadOnlyList<object> path, JsonNode? value)
    {
        var doc = Document.Load(yaml);
        var found = doc.Find(path);
        if (found.Missing > 0 || Document.IsEmpty(found.Node!))
        {
            return Set(yaml, path, new JsonArray(value?.DeepClone()));
        }

        return found.Node is YamlSequenceNode list
            ? doc.Append(list, Flow(value))
            : throw Error($"{Display(path)} isn't a list.");
    }

    /// <summary>The file and its parse tree, with the node positions edits are made at.</summary>
    private sealed class Document
    {
        private readonly string _text;
        private readonly YamlNode? _root;
        private readonly string _newline;

        private Document(string text, YamlNode? root)
        {
            _text = text;
            _root = root;
            _newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        }

        public static Document Load(string text)
        {
            var stream = new YamlStream();
            try
            {
                stream.Load(new StringReader(text));
            }
            catch (YamlException e)
            {
                throw Error($"config.yaml:{e.Start.Line}: the file isn't valid YAML, so it can't be edited.");
            }

            var root = stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode;
            return new Document(text, root is not null && IsEmpty(root) ? null : root);
        }

        public static bool IsEmpty(YamlNode node) => node is YamlScalarNode { Style: ScalarStyle.Plain, Value: null or "" };

        /// <summary>Walks <paramref name="path"/> as far as it exists.</summary>
        public Found Find(IReadOnlyList<object> path)
        {
            YamlNode? node = _root;
            YamlNode? parent = null;
            YamlNode? key = null;
            for (var i = 0; i < path.Count; i++)
            {
                (YamlNode? Key, YamlNode? Value) next = (node, path[i]) switch
                {
                    (YamlMappingNode map, string name) =>
                        map.Children.FirstOrDefault(c => c.Key is YamlScalarNode { Value: { } k } && k == name) is { Key: not null } e
                            ? (e.Key, e.Value)
                            : (null, null),
                    (YamlSequenceNode list, int index) => index < list.Children.Count ? (null, list.Children[index]) : (null, null),
                    _ => (null, null),
                };
                if (next.Value is null)
                {
                    return new Found(node, parent, key, path.Count - i);
                }

                (parent, key, node) = (node, next.Key, next.Value);
            }

            return new Found(node, parent, key, 0);
        }

        /// <summary>Replaces the value <paramref name="found"/> points at, keeping the key and anything after it on the line.</summary>
        public string ReplaceValue(Found found, string flow)
        {
            if (found.Parent is null)
            {
                throw Error("the whole file can't be replaced by an edit.");
            }

            var (start, end) = found.Key is { } key
                ? (ColonAfter(key) + 1, EntryEnd(key, found.Node!))
                : (Index(found.Node!.Start), ContentEnd(found.Node!));
            var spaced = found.Key is null ? flow : " " + flow;
            return Splice(start, end, spaced);
        }

        public string InsertKey(YamlMappingNode map, string key, JsonNode? value)
        {
            if (map.Style == MappingStyle.Flow)
            {
                if (map.Children.Count == 0)
                {
                    return Splice(Index(map.Start), FlowEnd(Index(map.Start)), $"{{ {Key(key)}: {Flow(value)} }}");
                }

                var (lastKey, lastValue) = map.Children[^1];
                return Splice(EntryEnd(lastKey, lastValue), EntryEnd(lastKey, lastValue), $", {Key(key)}: {Flow(value)}");
            }

            var indent = Column(map.Children[0].Key);
            var (k, v) = map.Children[^1];
            var at = LineEnd(EntryEnd(k, v));
            return Splice(at, at, _newline + new string(' ', indent) + Block(key, value, indent));
        }

        public string AppendToRoot(string key, JsonNode? value)
        {
            var head = _text.TrimEnd();
            return (head.Length > 0 ? head + _newline : "") + Block(key, value, 0) + _newline;
        }

        public string Append(YamlSequenceNode list, string flow)
        {
            if (list.Style == SequenceStyle.Flow)
            {
                return list.Children.Count == 0
                    ? Splice(Index(list.Start), FlowEnd(Index(list.Start)), $"[{flow}]")
                    : Splice(ContentEnd(list.Children[^1]), ContentEnd(list.Children[^1]), $", {flow}");
            }

            var last = list.Children[^1];
            var dash = _text.LastIndexOf('-', Index(last.Start) - 1);
            var at = LineEnd(ContentEnd(last));
            return Splice(at, at, _newline + new string(' ', dash - LineStart(dash)) + "- " + flow);
        }

        public string RemoveEntry(Found found)
        {
            var node = found.Node!;
            if (found.Parent is YamlMappingNode map)
            {
                var index = map.Children.ToList().FindIndex(c => ReferenceEquals(c.Key, found.Key));
                if (map.Style != MappingStyle.Flow)
                {
                    return RemoveLines(Index(found.Key!.Start), EntryEnd(found.Key!, node));
                }

                return index < map.Children.Count - 1
                    ? Splice(Index(found.Key!.Start), Index(map.Children.ElementAt(index + 1).Key.Start), "")
                    : Splice(EntryEnd(map.Children.ElementAt(index - 1).Key, map.Children.ElementAt(index - 1).Value), EntryEnd(found.Key!, node), "");
            }

            var list = (YamlSequenceNode)found.Parent!;
            var at = list.Children.IndexOf(node);
            if (list.Style != SequenceStyle.Flow)
            {
                return RemoveLines(_text.LastIndexOf('-', Index(node.Start) - 1), ContentEnd(node));
            }

            return at < list.Children.Count - 1
                ? Splice(Index(node.Start), Index(list.Children[at + 1].Start), "")
                : Splice(ContentEnd(list.Children[at - 1]), ContentEnd(node), "");
        }

        // Whole lines, with any comment at the end of the last one.
        private string RemoveLines(int start, int end)
        {
            var from = LineStart(start);
            var to = LineEnd(end);
            to = to < _text.Length && _text[to] == '\r' ? to + 2 : to < _text.Length ? to + 1 : to;
            return Splice(from, Math.Min(to, _text.Length), "");
        }

        private string Splice(int start, int end, string replacement) =>
            string.Concat(_text.AsSpan(0, start), replacement, _text.AsSpan(end));

        /// <summary>Where a key's value ends; just after the colon when it has none.</summary>
        private int EntryEnd(YamlNode key, YamlNode value) => IsEmpty(value) ? ColonAfter(key) + 1 : ContentEnd(value);

        /// <summary>Where a node's own text ends: a block collection's last value, not the start of what follows it.</summary>
        private int ContentEnd(YamlNode node) => node switch
        {
            YamlMappingNode { Style: not MappingStyle.Flow, Children.Count: > 0 } map => EntryEnd(map.Children[^1].Key, map.Children[^1].Value),
            YamlSequenceNode { Style: not SequenceStyle.Flow, Children.Count: > 0 } list => ContentEnd(list.Children[^1]),
            YamlMappingNode or YamlSequenceNode => FlowEnd(Index(node.Start)),
            YamlScalarNode { Style: ScalarStyle.Literal or ScalarStyle.Folded } => TrimBack(Index(node.End)),
            _ when IsEmpty(node) => Index(node.Start),
            _ => Index(node.End),
        };

        /// <summary>
        /// Just past the bracket that closes the flow collection opening at <paramref name="start"/>. The parser's end
        /// mark for a collection is its start, so the text is scanned, skipping quoted strings.
        /// </summary>
        private int FlowEnd(int start)
        {
            var depth = 0;
            for (var i = start; i < _text.Length; i++)
            {
                switch (_text[i])
                {
                    case '[' or '{':
                        depth++;
                        break;
                    case ']' or '}':
                        if (--depth == 0)
                        {
                            return i + 1;
                        }

                        break;
                    case '"':
                        while (++i < _text.Length && _text[i] != '"')
                        {
                            i += _text[i] == '\\' ? 1 : 0;
                        }

                        break;
                    case '\'':
                        // '' inside single quotes is an escaped quote: the loop closes and reopens.
                        while (++i < _text.Length && _text[i] != '\'')
                        {
                        }

                        break;
                }
            }

            return _text.Length;
        }

        private int TrimBack(int index)
        {
            while (index > 0 && char.IsWhiteSpace(_text[index - 1]))
            {
                index--;
            }

            return index;
        }

        private int ColonAfter(YamlNode key) => _text.IndexOf(':', Index(key.End));

        private int LineStart(int index) => index == 0 ? 0 : _text.LastIndexOf('\n', index - 1) + 1;

        /// <summary>The end of the line <paramref name="index"/> is on, before its line break.</summary>
        private int LineEnd(int index)
        {
            var newline = _text.IndexOf('\n', index);
            return newline < 0 ? _text.Length : newline > 0 && _text[newline - 1] == '\r' ? newline - 1 : newline;
        }

        private int Column(YamlNode node) => Index(node.Start) - LineStart(Index(node.Start));

        private static int Index(Mark mark) => checked((int)mark.Index);

        private string Block(string key, JsonNode? value, int indent)
        {
            var pad = new string(' ', indent);
            var text = new StringBuilder($"{Key(key)}:");
            switch (value)
            {
                case JsonObject { Count: > 0 } map:
                    foreach (var (name, child) in map)
                    {
                        text.Append(_newline).Append(pad).Append("  ").Append(Block(name, child, indent + 2));
                    }

                    break;
                case JsonArray { Count: > 0 } items:
                    foreach (var item in items)
                    {
                        text.Append(_newline).Append(pad).Append("  - ").Append(Flow(item));
                    }

                    break;
                default:
                    text.Append(' ').Append(Flow(value));
                    break;
            }

            return text.ToString();
        }
    }

    /// <param name="Node">The node at the path, or the deepest one on the way to it; null when the file is empty.</param>
    /// <param name="Parent">The map or list holding <paramref name="Node"/>.</param>
    /// <param name="Key">Its key when the parent is a map.</param>
    /// <param name="Missing">How many path segments, counted from the end, don't exist.</param>
    private sealed record Found(YamlNode? Node, YamlNode? Parent, YamlNode? Key, int Missing);

    /// <summary>A value on one line: flow maps and lists, plain scalars where YAML reads them back as the same string.</summary>
    internal static string Flow(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject map => map.Count == 0 ? "{}" : $"{{ {string.Join(", ", map.Select(e => $"{Key(e.Key)}: {Flow(e.Value)}"))} }}",
        JsonArray items => $"[{string.Join(", ", items.Select(Flow))}]",
        JsonValue v when v.TryGetValue<string>(out var s) => PlainSafe(s) ? s : v.ToJsonString(),
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        _ => value.ToJsonString(),
    };

    // Plain only when it can't be read as anything else: no number, bool or null, nothing YAML treats as syntax.
    private static bool PlainSafe(string s) =>
        PlainPattern().IsMatch(s)
        && !NumberPattern().IsMatch(s)
        && s.ToUpperInvariant() is not ("NULL" or "TRUE" or "FALSE");

    private static string Key(string key) => KeyPattern().IsMatch(key) ? key : throw Error($"'{key}' isn't a config key.");

    /// <summary><c>defaults.review.mode</c>, <c>repos[2].overrides</c> → <c>defaults, review, mode</c>, <c>repos, 2, overrides</c>.</summary>
    internal static IReadOnlyList<object> ParsePath(string path)
    {
        var segments = new List<object>();
        if (path.Length == 0)
        {
            return segments;
        }

        foreach (var part in path.Split('.'))
        {
            var match = SegmentPattern().Match(part);
            if (!match.Success)
            {
                throw Error($"'{path}' isn't a config path; use keys and list indexes, as in repos[0].overrides.review.mode.");
            }

            segments.Add(match.Groups["key"].Value);
            segments.AddRange(match.Groups["index"].Captures.Select(c => (object)int.Parse(c.Value, CultureInfo.InvariantCulture)));
        }

        return segments;
    }

    private static string Display(IEnumerable<object> path) =>
        string.Concat(path.Select((s, i) => s is int n ? $"[{n}]" : i == 0 ? (string)s : $".{s}"));

    private static ConfigException Error(string message) => new([message]);

    [GeneratedRegex(@"^(?<key>[A-Za-z][A-Za-z0-9-]*)(\[(?<index>[0-9]+)\])*$")]
    private static partial Regex SegmentPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9-]*$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_./@+-]*$")]
    private static partial Regex PlainPattern();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex NumberPattern();
}
