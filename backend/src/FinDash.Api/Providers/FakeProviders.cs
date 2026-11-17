using FinDash.Core.Interfaces;

namespace FinDash.Api.Providers;

/// <summary>
/// Precios de ejemplo para pruebas, para el desarrollo sin llave y para la demo si CoinGecko
/// falla (riesgo de la sección 14). Usa los mismos valores que los mocks de la app.
/// </summary>
public sealed class FakeMarketDataProvider : IMarketDataProvider
{
    static readonly Dictionary<string, (decimal Price, decimal Change)> Base = new()
    {
        ["bitcoin"] = (95_205m, 2.1m), ["ethereum"] = (3_459.60m, -1.3m), ["tether"] = (1.00m, 0m),
        ["usd-coin"] = (1.00m, 0m), ["binancecoin"] = (640.25m, 0.8m), ["solana"] = (100.60m, 3.6m),
        ["ripple"] = (2.15m, -2.4m), ["cardano"] = (0.7210m, 1.1m), ["dogecoin"] = (0.1840m, -3.2m),
        ["tron"] = (0.2410m, 0.4m), ["avalanche-2"] = (36.80m, -0.9m), ["chainlink"] = (19.45m, 1.7m),
        ["polkadot"] = (6.92m, -1.1m), ["litecoin"] = (92.30m, 0.5m), ["bitcoin-cash"] = (455.10m, 0.2m),
        ["stellar"] = (0.3920m, -0.6m),
    };

    readonly Random _rng = new(17);
    readonly Lock _lock = new();
    readonly Dictionary<string, decimal> _drift = [];

    public Task<IReadOnlyDictionary<string, CryptoQuote>> GetQuotesAsync(IReadOnlyCollection<string> assetIds, CancellationToken ct)
    {
        var result = new Dictionary<string, CryptoQuote>();
        lock (_lock)
        {
            foreach (var id in assetIds)
            {
                if (!Base.TryGetValue(id, out var b)) continue;
                var drift = _drift.GetValueOrDefault(id, 1m);
                if (b.Change != 0) drift *= 1 + (decimal)(_rng.NextDouble() - 0.5) * 0.003m;
                _drift[id] = drift;
                result[id] = new CryptoQuote(id, Math.Round(b.Price * drift, b.Price < 10 ? 4 : 2), b.Change);
            }
        }
        return Task.FromResult<IReadOnlyDictionary<string, CryptoQuote>>(result);
    }

    public Task<IReadOnlyList<(DateTimeOffset Time, decimal PriceUsd)>> GetHistoryAsync(string assetId, int days, CancellationToken ct)
    {
        var last = Base.TryGetValue(assetId, out var b) ? b.Price : 1m;
        var count = days <= 1 ? 48 : days <= 7 ? 84 : days <= 30 ? 60 : 52;
        var step = TimeSpan.FromDays(days) / count;
        var rng = new Random(assetId.GetHashCode(StringComparison.Ordinal) ^ days);
        var prices = new decimal[count];
        prices[^1] = last;
        for (var i = count - 1; i > 0; i--)
            prices[i - 1] = prices[i] / (1 + (decimal)(rng.NextDouble() - 0.48) * 0.024m);

        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<(DateTimeOffset, decimal)> points = prices
            .Select((p, i) => (now - step * (count - 1 - i), Math.Round(p, 6)))
            .ToList();
        return Task.FromResult(points);
    }
}

public sealed class FakeExchangeRateProvider : IExchangeRateProvider
{
    public Task<IReadOnlyList<DailyRate>> GetRatesAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rates = new List<DailyRate>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            // Movimiento suave y repetible: entre ~₡498 y ~₡508.
            var wave = (decimal)Math.Sin(d.DayNumber / 9.0) * 5m;
            var buy = Math.Round(503.12m + wave, 2);
            rates.Add(new DailyRate(d, buy, buy + 6.36m));
        }
        return Task.FromResult<IReadOnlyList<DailyRate>>(rates);
    }
}
