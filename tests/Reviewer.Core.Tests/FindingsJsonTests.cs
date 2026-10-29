using System.Text.Json;
using Reviewer.Core.Findings;

namespace Reviewer.Core.Tests;

public class FindingsJsonTests
{
    private const string ValidReport = """
        {
          "summary": "One issue.",
          "findings": [
            {
              "id": "f1",
              "severity": "major",
              "category": "correctness",
              "confidence": 0.8,
              "file": "src/Foo.cs",
              "startLine": 10,
              "endLine": 14,
              "title": "Off-by-one",
              "body": "Loop skips the last item."
            }
          ]
        }
        """;

    [Fact]
    public void Deserialize_ReadsSchemaShapedReport()
    {
        var report = FindingsJson.Deserialize(ValidReport);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(Severity.Major, finding.Severity);
        Assert.Equal(Category.Correctness, finding.Category);
        Assert.Null(finding.Suggestion);
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var report = FindingsJson.Deserialize(ValidReport);

        Assert.Equal(report.Findings[0], FindingsJson.Deserialize(FindingsJson.Serialize(report)).Findings[0]);
    }

    [Theory]
    [InlineData("\"severity\": \"major\"", "\"severity\": \"critical\"")]
    [InlineData("\"severity\": \"major\"", "\"severity\": 2")]
    [InlineData("\"title\": \"Off-by-one\",", "\"title\": \"Off-by-one\", \"fingerprint\": \"x\",")]
    [InlineData("\"file\": \"src/Foo.cs\",", "")]
    public void Deserialize_RejectsOffSchemaInput(string from, string to)
    {
        Assert.Throws<JsonException>(() => FindingsJson.Deserialize(ValidReport.Replace(from, to, StringComparison.Ordinal)));
    }

    [Fact]
    public void Schema_IsEmbedded()
    {
        using var doc = JsonDocument.Parse(FindingsSchema.Json);

        Assert.Equal("FindingsReport", doc.RootElement.GetProperty("title").GetString());
    }
}
