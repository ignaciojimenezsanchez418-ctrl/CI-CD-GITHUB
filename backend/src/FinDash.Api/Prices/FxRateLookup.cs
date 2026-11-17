using FinDash.Api.Data;
using FinDash.Core.Interfaces;
using FinDash.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace FinDash.Api.Prices;

/// <summary>
/// Tipo de cambio de compra de una fecha, para guardar en cada transacción en USD.
/// Busca primero en PriceSnapshots y si no hay (fechas antes de que arrancara el servidor)
/// lo pide a la fuente; como último recurso usa el de hoy.
/// </summary>
public sealed class FxRateLookup(FinDashDbContext db, IExchangeRateProvider fx, QuoteStore store, ILogger<FxRateLookup> log)
{
    public async Task<decimal?> GetBuyRateAsync(DateOnly date, CancellationToken ct)
    {
        var from = new DateTimeOffset(date.AddDays(-6).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var saved = await db.PriceSnapshots
            .Where(s => s.AssetId == AssetIds.Usd && s.Timestamp >= from && s.Timestamp < to)
            .OrderByDescending(s => s.Timestamp)
            .Select(s => (decimal?)s.Price)
            .FirstOrDefaultAsync(ct);
        if (saved is not null) return saved;

        try
        {
            // Una semana hacia atrás cubre fines de semana y feriados sin publicación.
            var rates = await fx.GetRatesAsync(date.AddDays(-7), date, ct);
            if (rates.Count > 0) return rates[^1].Buy;
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning("No se pudo obtener el tipo de cambio del {Date}: {Message}", date, ex.Message);
        }

        return store.Current.Fx?.Buy;
    }
}
