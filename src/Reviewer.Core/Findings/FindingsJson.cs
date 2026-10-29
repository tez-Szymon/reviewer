using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reviewer.Core.Findings;

/// <summary>Serializer settings matching the findings schema: camelCase names, lowercase enum strings.</summary>
public static class FindingsJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static FindingsReport Deserialize(string json) =>
        JsonSerializer.Deserialize<FindingsReport>(json, Options)
        ?? throw new JsonException("Findings report is null.");

    public static string Serialize(FindingsReport report) => JsonSerializer.Serialize(report, Options);
}
