namespace Shop.Billing;

public sealed record InvoiceLine(decimal UnitPrice, int Quantity, decimal TaxRate);

public static class InvoiceTotals
{
    /// <summary>The invoice total with tax, rounded to cents.</summary>
    public static decimal Total(IEnumerable<InvoiceLine> lines)
    {
        decimal total = 0;
        foreach (var line in lines)
        {
            var net = line.UnitPrice * line.Quantity;
            total += net + net * line.TaxRate;
        }

        return Math.Round(total, 2, MidpointRounding.AwayFromZero);
    }
}
