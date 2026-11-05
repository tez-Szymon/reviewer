namespace Shop.Export;

/// <summary>Writes every order to CSV, a page at a time, so a large shop's orders aren't all loaded at once.</summary>
public sealed class OrderExport(IOrderStore store, ICsvWriter writer)
{
    public const int PageSize = 500;

    public async Task ExportAsync(CancellationToken ct)
    {
        var total = await store.CountAsync(ct);
        var pages = total / PageSize;
        for (var page = 0; page < pages; page++)
        {
            var orders = await store.ListAsync(page * PageSize, PageSize, ct);
            foreach (var order in orders)
            {
                writer.Write(order.Id, order.Customer, order.Total);
            }
        }
    }
}
