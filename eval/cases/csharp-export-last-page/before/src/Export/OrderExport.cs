namespace Shop.Export;

/// <summary>Writes every order to CSV.</summary>
public sealed class OrderExport(IOrderStore store, ICsvWriter writer)
{
    public async Task ExportAsync(CancellationToken ct)
    {
        var orders = await store.ListAsync(0, int.MaxValue, ct);
        foreach (var order in orders)
        {
            writer.Write(order.Id, order.Customer, order.Total);
        }
    }
}
