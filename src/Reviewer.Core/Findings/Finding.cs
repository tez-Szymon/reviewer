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

/// <summary>The agent's whole output. The engine never trusts it before schema validation.</summary>
public sealed record FindingsReport(string Summary, IReadOnlyList<Finding> Findings);
