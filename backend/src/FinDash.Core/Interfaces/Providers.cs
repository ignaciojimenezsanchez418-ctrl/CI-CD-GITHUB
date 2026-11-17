using FinDash.Core.Models;

namespace FinDash.Core.Interfaces;

/// <summary>Precio de una cripto en dólares y su variación de 24 h.</summary>
public sealed record CryptoQuote(string AssetId, decimal PriceUsd, decimal Change24hPct);

/// <summary>Tipo de cambio oficial de un día (BCCR 317 compra, 318 venta).</summary>
public sealed record DailyRate(DateOnly Date, decimal Buy, decimal Sell);

/// <summary>Fuente de precios cripto (CoinGecko o un proveedor falso para pruebas y demo).</summary>
public interface IMarketDataProvider
{
    /// <summary>Precios actuales de varios activos en UNA sola llamada.</summary>
    Task<IReadOnlyDictionary<string, CryptoQuote>> GetQuotesAsync(IReadOnlyCollection<string> assetIds, CancellationToken ct);

    /// <summary>Historial en USD de los últimos <paramref name="days"/> días.</summary>
    Task<IReadOnlyList<(DateTimeOffset Time, decimal PriceUsd)>> GetHistoryAsync(string assetId, int days, CancellationToken ct);
}

/// <summary>Fuente del tipo de cambio USD/CRC (BCCR o un proveedor falso).</summary>
public interface IExchangeRateProvider
{
    /// <summary>Tipos de cambio diarios entre dos fechas, ordenados por fecha.</summary>
    Task<IReadOnlyList<DailyRate>> GetRatesAsync(DateOnly from, DateOnly to, CancellationToken ct);
}
