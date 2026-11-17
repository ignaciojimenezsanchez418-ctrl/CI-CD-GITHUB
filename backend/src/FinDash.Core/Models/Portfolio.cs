namespace FinDash.Core.Models;

public enum TxType { Buy, Sell }

/// <summary>Una compra o venta registrada por el usuario (RF-06).</summary>
public sealed record Transaction
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string AssetId { get; init; }
    public TxType Type { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }

    /// <summary>Moneda de UnitPrice y de Fee.</summary>
    public Currency PriceCurrency { get; init; }

    public decimal Fee { get; init; }
    public DateOnly Date { get; init; }

    /// <summary>
    /// Colones por dólar EN LA FECHA de la transacción. Se usa para pasar a colones los montos
    /// en USD. Si se usara el tipo de cambio de hoy, la ganancia en colones del ahorro en
    /// dólares siempre daría cero.
    /// </summary>
    public decimal FxRate { get; init; } = 1m;

    /// <summary>Desempata transacciones del mismo día.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Precio actual de un activo en colones y su variación de 24 h.</summary>
public sealed record AssetPrice(decimal PriceCrc, decimal Change24hPct);

/// <summary>Posición de un activo ya valorada (RF-08). Montos en colones.</summary>
public sealed record Holding(
    string AssetId,
    decimal Quantity,
    decimal AvgCostCrc,
    decimal InvestedCrc,
    decimal ValueCrc,
    decimal PnlCrc,
    decimal PnlPct,
    decimal RealizedPnlCrc,
    decimal Change24hPct);

/// <summary>Resumen del portafolio. Montos en colones; FxRate permite mostrarlos en USD.</summary>
public sealed record PortfolioSummary(
    decimal ValueCrc,
    decimal InvestedCrc,
    decimal PnlCrc,
    decimal PnlPct,
    decimal RealizedPnlCrc,
    decimal DayChangeCrc,
    decimal DayChangePct,
    decimal FxRate,
    IReadOnlyList<Holding> Holdings)
{
    public decimal ValueUsd => ToUsd(ValueCrc);
    public decimal InvestedUsd => ToUsd(InvestedCrc);
    public decimal PnlUsd => ToUsd(PnlCrc);

    decimal ToUsd(decimal crc) => FxRate == 0 ? 0 : Math.Round(crc / FxRate, 2);
}
