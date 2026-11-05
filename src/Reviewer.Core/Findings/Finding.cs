namespace Reviewer.Core.Findings;

public enum Severity
{
    Nit,
    Minor,
    Major,
    Blocker,
}

public enum Category
{
    Security,
    Correctness,
    Performance,
    Tests,
    Maintainability,
    Style,
    Docs,
}

/// <summary>One issue reported by the agent. Line numbers refer to the PR head.</summary>
public sealed record Finding(
    string Id,
    Severity Severity,
    Category Category,
    double Confidence,
    string File,
    int StartLine,
    int EndLine,
    string Title,
    string Body,
    string? Suggestion = null);

public enum RiskLevel
{
    Low,
    Medium,
    High,
}

/// <summary>How risky the change is overall, and why in one sentence.</summary>
public sealed record ReviewRisk(RiskLevel Level, string Reason);

/// <summary>One area the change touches, e.g. "Settings UI", and what changed there.</summary>
public sealed record ChangeArea(string Area, string Description);

/// <summary>The agent's whole output. The engine never trusts it before schema validation.</summary>
/// <param name="Summary">What the change does and whether it does what its description says.</param>
/// <param name="Changes">The change grouped by area. Null from agents or reports that predate it.</param>
/// <param name="Checked">What the reviewer confirmed, beyond the findings.</param>
/// <param name="NotVerified">What the reviewer couldn't confirm, e.g. runtime behaviour.</param>
public sealed record FindingsReport(
    string Summary,
    IReadOnlyList<Finding> Findings,
    ReviewRisk? Risk = null,
    IReadOnlyList<ChangeArea>? Changes = null,
    IReadOnlyList<string>? Checked = null,
    IReadOnlyList<string>? NotVerified = null);
