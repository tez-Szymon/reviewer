namespace Shop.Rates;

/// <summary>Exchange rates, fetched once per currency and kept for the life of the app.</summary>
public sealed class RateCache(IRateClient client)
{
    private readonly Dictionary<string, decimal> _rates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<decimal> GetAsync(string currency, CancellationToken ct)
    {
        if (_rates.TryGetValue(currency, out var rate))
        {
            return rate;
        }

        rate = await client.FetchAsync(currency, ct);
        _rates[currency] = rate;
        return rate;
    }
}
