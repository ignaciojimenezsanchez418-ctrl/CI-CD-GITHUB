using FinDash.Core.Interfaces;
using FinDash.Core.Models;
using Microsoft.Extensions.Caching.Memory;

namespace FinDash.Api.Prices;

/// <summary>
/// Gráfica histórica (RF-09). PriceSnapshots solo tiene datos desde que arrancó el servidor,
/// así que el historial se pide a la fuente cuando alguien abre la gráfica y se guarda en
/// memoria un rato para no gastar llamadas.
/// </summary>
public sealed class HistoryService(
    IMarketDataProvider market,
    IExchangeRateProvider fx,
    QuoteStore store,
    IMemoryCache cache,
    TimeProvider time)
{
    public async Task<IReadOnlyList<PricePoint>?> GetAsync(string assetId, ChartRange range, CancellationToken ct)
    {
        var key = $"history:{assetId}:{range}";
        if (cache.TryGetValue(key, out IReadOnlyList<PricePoint>? cached)) return cached;

        var points = assetId == AssetIds.Usd
            ? await FxHistoryAsync(range, ct)
            : await CryptoHistoryAsync(assetId, range, ct);
        if (points is null) return null;

        cache.Set(key, points, range == ChartRange.Day ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(1));
        return points;
    }

    async Task<IReadOnlyList<PricePoint>?> CryptoHistoryAsync(string assetId, ChartRange range, CancellationToken ct)
    {
        // Las cripto se pasan a colones con el tipo de cambio de hoy: la variación de la
        // gráfica es la del precio en USD (simplificación aceptable para la gráfica).
        var fxBuy = store.Current.Fx?.Buy;
        if (fxBuy is null) return null;

        var history = await market.GetHistoryAsync(assetId, Days(range), ct);
        return history.Select(p => new PricePoint(p.Time, p.PriceUsd * fxBuy.Value, p.PriceUsd)).ToList();
    }

    async Task<IReadOnlyList<PricePoint>> FxHistoryAsync(ChartRange range, CancellationToken ct)
    {
        // El BCCR publica un valor por día; para "24h" se muestran los últimos días.
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var days = range == ChartRange.Day ? 7 : Days(range);
        var rates = await fx.GetRatesAsync(today.AddDays(-days), today, ct);
        return rates
            .Select(r => new PricePoint(new DateTimeOffset(r.Date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), r.Buy, 1m))
            .ToList();
    }

    static int Days(ChartRange range) => range switch
    {
        ChartRange.Day => 1,
        ChartRange.Week => 7,
        ChartRange.Month => 30,
        _ => 365,
    };
}
