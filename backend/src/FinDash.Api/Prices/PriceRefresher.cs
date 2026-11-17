using FinDash.Api.Data;
using FinDash.Api.Providers;
using FinDash.Core.Interfaces;
using FinDash.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace FinDash.Api.Prices;

/// <summary>
/// Trae precios y tipo de cambio de las fuentes externas, los guarda en PriceSnapshots y
/// actualiza la caché. Es la ÚNICA clase que llama a CoinGecko y al BCCR para precios (sección 9).
/// </summary>
public sealed class PriceRefresher(
    FinDashDbContext db,
    QuoteStore store,
    IMarketDataProvider market,
    IExchangeRateProvider fx,
    TimeProvider time,
    ILogger<PriceRefresher> log)
{
    /// <summary>Al arrancar: carga la última foto guardada para no empezar sin datos.</summary>
    public async Task LoadFromDatabaseAsync(CancellationToken ct)
    {
        var latest = await db.PriceSnapshots
            .GroupBy(s => s.AssetId)
            .Select(g => g.OrderByDescending(s => s.Timestamp).First())
            .ToListAsync(ct);
        if (latest.Count == 0) return;

        var usd = latest.FirstOrDefault(s => s.AssetId == AssetIds.Usd);
        var crypto = latest.Where(s => s.AssetId != AssetIds.Usd)
            .ToDictionary(s => s.AssetId, s => new CryptoQuote(s.AssetId, s.Price, s.Change24hPct));

        store.Update(_ => new MarketState(
            usd is null ? null : new FxQuote(usd.Price, usd.SellPrice ?? usd.Price, usd.Change24hPct, DateOnly.FromDateTime(usd.Timestamp.UtcDateTime)),
            crypto,
            crypto.Count == 0 ? null : latest.Where(s => s.AssetId != AssetIds.Usd).Max(s => s.Timestamp),
            usd?.Timestamp,
            LastRefreshFailed: false));
        log.LogInformation("Precios cargados de la base: {Count} activos", latest.Count);
    }

    /// <summary>Un solo pedido a CoinGecko con todas las cripto del catálogo.</summary>
    public async Task<bool> RefreshCryptoAsync(CancellationToken ct)
    {
        var ids = await db.Assets.Where(a => a.Kind == AssetKind.Crypto).Select(a => a.Id).ToListAsync(ct);
        try
        {
            var quotes = await market.GetQuotesAsync(ids, ct);
            var now = time.GetUtcNow();

            db.PriceSnapshots.AddRange(quotes.Values.Select(q => new PriceSnapshot
            {
                AssetId = q.AssetId,
                Price = q.PriceUsd,
                Currency = Currency.USD,
                Change24hPct = q.Change24hPct,
                Timestamp = now,
            }));
            await db.SaveChangesAsync(ct);

            store.Update(s =>
            {
                // Se conservan los activos que esta vez no vinieron.
                var merged = new Dictionary<string, CryptoQuote>(s.Crypto);
                foreach (var (id, q) in quotes) merged[id] = q;
                return s with { Crypto = merged, CryptoUpdatedAt = now, LastRefreshFailed = false };
            });
            return true;
        }
        catch (Exception ex) when (ex is RateLimitedException or HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
        {
            log.LogWarning("No se pudieron actualizar los precios cripto: {Message}. Se sirve la caché.", ex.Message);
            store.Update(s => s with { LastRefreshFailed = true });
            return false;
        }
    }

    /// <summary>Tipo de cambio de los últimos días; la variación es contra el día hábil anterior.</summary>
    public async Task<bool> RefreshFxAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        try
        {
            var rates = await fx.GetRatesAsync(today.AddDays(-7), today, ct);
            if (rates.Count == 0)
            {
                log.LogWarning("El proveedor del tipo de cambio no devolvió datos.");
                return false;
            }

            var last = rates[^1];
            var previous = rates.Count > 1 ? rates[^2] : last;
            var change = previous.Buy == 0 ? 0 : Math.Round((last.Buy - previous.Buy) / previous.Buy * 100, 4);
            var now = time.GetUtcNow();

            db.PriceSnapshots.Add(new PriceSnapshot
            {
                AssetId = AssetIds.Usd,
                Price = last.Buy,
                SellPrice = last.Sell,
                Currency = Currency.CRC,
                Change24hPct = change,
                Timestamp = now,
            });
            await db.SaveChangesAsync(ct);

            store.Update(s => s with { Fx = new FxQuote(last.Buy, last.Sell, change, last.Date), FxUpdatedAt = now });
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
        {
            log.LogWarning("No se pudo actualizar el tipo de cambio: {Message}. Se usa el último guardado.", ex.Message);
            store.Update(s => s with { LastRefreshFailed = true });
            return false;
        }
    }
}
