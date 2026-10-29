namespace Reviewer.Core.Findings;

/// <summary>The JSON schema written to each job folder as <c>schema.json</c>.</summary>
public static class FindingsSchema
{
    public static string Json { get; } = Load();

    private static string Load()
    {
        using var stream = typeof(FindingsSchema).Assembly.GetManifestResourceStream("Reviewer.Core.findings.schema.json")
            ?? throw new InvalidOperationException("Embedded findings schema is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
