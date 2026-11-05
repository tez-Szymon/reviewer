using Shop.Billing;

public class InvoiceTotalsTests
{
    [Fact]
    public void Total_AddsTaxAndRoundsToCents() =>
        Assert.Equal(24.69m, InvoiceTotals.Total([new(10.005m, 2, 0.234m)]));

    [Fact]
    public void Gross_IsUnrounded() =>
        Assert.Equal(24.69234m, InvoiceTotals.Gross(new(10.005m, 2, 0.234m)));

    [Fact]
    public void Total_OfNoLines_IsZero() =>
        Assert.Equal(0m, InvoiceTotals.Total([]));
}
