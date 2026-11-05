namespace Shop.Export;

public sealed record Order(int Id, string Customer, decimal Total);

public interface IOrderStore
{
    Task<int> CountAsync(CancellationToken ct);

    /// <summary>Orders by id, skipping <paramref name="skip"/> and returning at most <paramref name="take"/>.</summary>
    Task<IReadOnlyList<Order>> ListAsync(int skip, int take, CancellationToken ct);
}

public interface ICsvWriter
{
    void Write(params object[] values);
}
