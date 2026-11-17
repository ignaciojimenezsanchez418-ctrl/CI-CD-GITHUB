using FinDash.Core.Interfaces;
using FinDash.Core.Models;

namespace FinDash.Api.Prices;

/// <summary>Tipo de cambio vigente y su variación respecto al día anterior.</summary>
public sealed record FxQuote(decimal Buy, decimal Sell, decimal Change24hPct, DateOnly Date);

/// <summary>Precio de un activo listo para la app: en USD y en colones (con el tipo de cambio de compra).</summary>
public sealed record MarketQuote(string AssetId, decimal PriceUsd, decimal PriceCrc, decimal Change24hPct);

/// <summary>Foto de los precios en memoria. Inmutable: se reemplaza entera en cada refresco.</summary>
public sealed record MarketState(
    FxQuote? Fx,
    IReadOnlyDictionary<string, CryptoQuote> Crypto,
    DateTimeOffset? CryptoUpdatedAt,
    DateTimeOffset? FxUpdatedAt,
    bool LastRefreshFailed)
{
    public static readonly MarketState Empty = new(null, new Dictionary<string, CryptoQuote>(), null, null, false);

    /// <summary>Precio del activo en USD y CRC, o null si todavía no hay datos.</summary>
    public MarketQuote? Quote(string assetId)
    {
        if (Fx is null) return null;
        if (assetId == AssetIds.Usd) return new MarketQuote(assetId, 1m, Fx.Buy, Fx.Change24hPct);
        return Crypto.TryGetValue(assetId, out var q)
            ? new MarketQuote(assetId, q.PriceUsd, q.PriceUsd * Fx.Buy, q.Change24hPct)
            : null;
    }

    /// <summary>Precios en colones para el PortfolioCalculator.</summary>
    public Dictionary<string, AssetPrice> PricesCrc()
    {
        var result = new Dictionary<string, AssetPrice>();
        if (Fx is null) return result;
        result[AssetIds.Usd] = new AssetPrice(Fx.Buy, Fx.Change24hPct);
        foreach (var q in Crypto.Values)
            result[q.AssetId] = new AssetPrice(q.PriceUsd * Fx.Buy, q.Change24hPct);
        return result;
    }
}

/// <summary>
/// Caché de precios compartida por todos los usuarios (sección 4: "una sola caché de
/// precios"). Solo PriceRefresher la escribe; los endpoints y el AlertWorker la leen.
/// </summary>
public sealed class QuoteStore
{
    MarketState _state = MarketState.Empty;

    public MarketState Current => Volatile.Read(ref _state);

    public void Update(Func<MarketState, MarketState> change)
    {
        MarketState before, after;
        do
        {
            before = Current;
            after = change(before);
        }
        while (Interlocked.CompareExchange(ref _state, after, before) != before);
    }
}
