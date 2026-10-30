namespace Reviewer.Instructions;

/// <summary>Instruction layers that ship with the app and change only through releases.</summary>
public static class BuiltInInstructions
{
    /// <summary>Layer 1: always first, not replaceable.</summary>
    public static string CoreContract { get; } = Load("core-contract.md");

    /// <summary>Layer 2: what good review covers. The user can disable it per repo.</summary>
    public static string DefaultReviewGuide { get; } = Load("default-review-guide.md");

    /// <summary>Layer 6: always last.</summary>
    public static string CoreContractRestated { get; } = Load("core-contract-restated.md");

    private static string Load(string name)
    {
        using var stream = typeof(BuiltInInstructions).Assembly.GetManifestResourceStream($"Reviewer.Instructions.{name}")
            ?? throw new InvalidOperationException($"Embedded instruction '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
