namespace Shop.Billing;

public sealed record InvoiceLine(decimal UnitPrice, int Quantity, decimal TaxRate);

public static class InvoiceTotals
{
    /// <summary>The invoice total with tax, rounded to cents.</summary>
    public static decimal Total(IEnumerable<InvoiceLine> lines) =>
        Math.Round(lines.Sum(Gross), 2, MidpointRounding.AwayFromZero);

    /// <summary>One line's price with tax, unrounded: rounding each line would let cents drift from the total.</summary>
    public static decimal Gross(InvoiceLine line)
    {
        var net = line.UnitPrice * line.Quantity;
        return net + net * line.TaxRate;
    }
}
