using Reviewer.Core.Findings;

namespace Reviewer.Core.Tests;

public class FindingFingerprintTests
{
    private static Finding Finding(Category category = Category.Correctness, string file = "src/a.cs", string title = "t") =>
        new("f1", Severity.Major, category, 0.9, file, 3, 4, title, "body");

    [Fact]
    public void Compute_IgnoresWhitespaceLineEndingsAndWording()
    {
        var a = FindingFingerprint.Compute(Finding(title: "one"), "if (x)\n    y();\n");
        var b = FindingFingerprint.Compute(Finding(title: "two"), "  if  (x)\r\n\r\n\ty();");

        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{24}$", a);
    }

    [Fact]
    public void Compute_DiffersByCategoryFileAndCode()
    {
        var baseline = FindingFingerprint.Compute(Finding(), "y();");

        Assert.NotEqual(baseline, FindingFingerprint.Compute(Finding(Category.Security), "y();"));
        Assert.NotEqual(baseline, FindingFingerprint.Compute(Finding(file: "src/b.cs"), "y();"));
        Assert.NotEqual(baseline, FindingFingerprint.Compute(Finding(), "z();"));
    }
}
