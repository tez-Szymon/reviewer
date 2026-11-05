using Shop.Rates;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<IRateClient, HttpRateClient>();
builder.Services.AddSingleton<RateCache>();

var app = builder.Build();
app.MapGet("/price/{sku}", async (string sku, string currency, RateCache rates, IPriceList prices, CancellationToken ct) =>
    prices.Get(sku) * await rates.GetAsync(currency, ct));
app.Run();
