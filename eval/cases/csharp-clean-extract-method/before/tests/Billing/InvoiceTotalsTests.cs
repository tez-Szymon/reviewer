using Shop.Billing;

public class InvoiceTotalsTests
{
    [Fact]
    public void Total_AddsTaxAndRoundsToCents() =>
        Assert.Equal(24.69m, InvoiceTotals.Total([new(10.005m, 2, 0.234m)]));
}
