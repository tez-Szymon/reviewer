namespace Shop.Rates;

/// <summary>Exchange rates, fetched on every call.</summary>
public sealed class RateCache(IRateClient client)
{
    public Task<decimal> GetAsync(string currency, CancellationToken ct) => client.FetchAsync(currency, ct);
}
