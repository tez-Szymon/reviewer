using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Reviewer.Engine.Config;

/// <summary>
/// Turns YAML into a JSON tree, so merging and binding work on one model. Plain scalars are typed by the YAML 1.2 core
/// schema (<c>true</c>, <c>12</c>, <c>0.6</c>, <c>null</c>); quoted ones stay strings.
/// </summary>
internal static partial class YamlToJson
{
    /// <param name="Lines">Line of each key or list item, by display path such as <c>defaults.review.mode</c>.</param>
    public sealed record Result(JsonObject Root, IReadOnlyDictionary<string, int> Lines);

    /// <exception cref="ConfigException">The text isn't YAML, or its top level isn't a map.</exception>
    public static Result Convert(string yaml, string source)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException e)
        {
            throw new ConfigException([$"{source}:{e.Start.Line}: {Inner(e).Message}"]);
        }

        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" })
        {
            return new Result([], lines);
        }

        if (stream.Documents.Count > 1)
        {
            throw new ConfigException([$"{source}:{stream.Documents[1].RootNode.Start.Line}: only one YAML document is allowed."]);
        }

        var errors = new List<string>();
        var root = ToNode(stream.Documents[0].RootNode, "", lines, errors, source);
        if (root is not JsonObject obj)
        {
            throw new ConfigException([$"{source}:{stream.Documents[0].RootNode.Start.Line}: the top level must be a map of settings."]);
        }

        return errors.Count > 0 ? throw new ConfigException(errors) : new Result(obj, lines);
    }

    private static JsonNode? ToNode(YamlNode node, string path, Dictionary<string, int> lines, List<string> errors, string source)
    {
        switch (node)
        {
            case YamlMappingNode map:
                var obj = new JsonObject();
                foreach (var (keyNode, valueNode) in map.Children)
                {
                    if (keyNode is not YamlScalarNode { Value: { Length: > 0 } key })
                    {
                        errors.Add($"{source}:{keyNode.Start.Line}: keys must be plain names.");
                        continue;
                    }

                    // YamlStream has already refused duplicate keys.
                    var child = path.Length == 0 ? key : $"{path}.{key}";
                    lines[child] = checked((int)keyNode.Start.Line);
                    obj[key] = ToNode(valueNode, child, lines, errors, source);
                }

                return obj;

            case YamlSequenceNode sequence:
                var array = new JsonArray();
                for (var i = 0; i < sequence.Children.Count; i++)
                {
                    var item = sequence.Children[i];
                    var child = $"{path}[{i}]";
                    lines[child] = checked((int)item.Start.Line);
                    array.Add(ToNode(item, child, lines, errors, source));
                }

                return array;

            case YamlScalarNode scalar:
                return scalar.Style is ScalarStyle.Plain ? Plain(scalar.Value ?? "") : JsonValue.Create(scalar.Value ?? "");

            default:
                errors.Add($"{source}:{node.Start.Line}: unsupported YAML node.");
                return null;
        }
    }

    private static JsonValue? Plain(string value)
    {
        if (value is "" or "~" or "null" or "Null" or "NULL")
        {
            return null;
        }

        if (value is "true" or "True" or "TRUE" or "false" or "False" or "FALSE")
        {
            return JsonValue.Create(value[0] is 't' or 'T');
        }

        if (IntegerPattern().IsMatch(value) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            return JsonValue.Create(integer);
        }

        if (FloatPattern().IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return JsonValue.Create(number);
        }

        return JsonValue.Create(value);
    }

    // YamlDotNet sometimes wraps the useful message in a generic one.
    private static Exception Inner(YamlException e) => e.InnerException is YamlException inner ? Inner(inner) : (Exception?)e.InnerException ?? e;

    [GeneratedRegex("^[-+]?[0-9]+$")]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatPattern();
}
