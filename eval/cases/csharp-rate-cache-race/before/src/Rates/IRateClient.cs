namespace Shop.Rates;

public interface IRateClient
{
    Task<decimal> FetchAsync(string currency, CancellationToken ct);
}
