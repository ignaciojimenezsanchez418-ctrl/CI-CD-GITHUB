using System.Net;
using System.Text.Json;
using FinDash.Core.Interfaces;

namespace FinDash.Api.Providers;

/// <summary>
/// Cliente de CoinGecko (plan Demo). La llave va en el encabezado x-cg-demo-api-key y vive
/// solo en la configuración del servidor (D13). Todas las cripto se piden en UNA llamada.
/// </summary>
public sealed class CoinGeckoProvider(HttpClient http, CallBudget budget, ILogger<CoinGeckoProvider> log)
    : IMarketDataProvider
{
    const string Api = "CoinGecko";

    public async Task<IReadOnlyDictionary<string, CryptoQuote>> GetQuotesAsync(IReadOnlyCollection<string> assetIds, CancellationToken ct)
    {
        if (assetIds.Count == 0) return new Dictionary<string, CryptoQuote>();
        var ids = Uri.EscapeDataString(string.Join(',', assetIds));
        var json = await GetAsync($"simple/price?ids={ids}&vs_currencies=usd&include_24hr_change=true", ct);
        return ParseSimplePrice(json);
    }

    public async Task<IReadOnlyList<(DateTimeOffset Time, decimal PriceUsd)>> GetHistoryAsync(string assetId, int days, CancellationToken ct)
    {
        var json = await GetAsync($"coins/{Uri.EscapeDataString(assetId)}/market_chart?vs_currency=usd&days={days}", ct);
        return ParseMarketChart(json);
    }

    async Task<string> GetAsync(string path, CancellationToken ct)
    {
        budget.Take(Api);
        using var response = await http.GetAsync(path, ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
            budget.BlockFor(wait);
            log.LogWarning("CoinGecko respondió 429; no se llamará por {Seconds} s", wait.TotalSeconds);
            throw new RateLimitedException("CoinGecko respondió 429.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>{"bitcoin":{"usd":95205,"usd_24h_change":2.1}, ...}</summary>
    internal static Dictionary<string, CryptoQuote> ParseSimplePrice(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, CryptoQuote>();
        foreach (var coin in doc.RootElement.EnumerateObject())
        {
            if (!coin.Value.TryGetProperty("usd", out var usd) || usd.ValueKind != JsonValueKind.Number) continue;
            var change = coin.Value.TryGetProperty("usd_24h_change", out var c) && c.ValueKind == JsonValueKind.Number
                ? ToDecimal(c)
                : 0m;
            result[coin.Name] = new CryptoQuote(coin.Name, ToDecimal(usd), Math.Round(change, 4));
        }
        return result;
    }

    /// <summary>{"prices":[[1731715200000, 91234.5], ...], ...}</summary>
    internal static List<(DateTimeOffset Time, decimal PriceUsd)> ParseMarketChart(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var points = new List<(DateTimeOffset, decimal)>();
        if (!doc.RootElement.TryGetProperty("prices", out var prices)) return points;
        foreach (var p in prices.EnumerateArray())
        {
            var ms = (long)p[0].GetDouble();
            points.Add((DateTimeOffset.FromUnixTimeMilliseconds(ms), ToDecimal(p[1])));
        }
        return points;
    }

    /// <summary>CoinGecko a veces manda notación científica (1.2e-05).</summary>
    static decimal ToDecimal(JsonElement e) =>
        e.TryGetDecimal(out var d) ? d : (decimal)e.GetDouble();
}
