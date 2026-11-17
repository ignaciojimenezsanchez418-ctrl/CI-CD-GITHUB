using FinDash.Core.Models;

namespace FinDash.Core.Services;

/// <summary>
/// Cálculo del portafolio con costo promedio ponderado (D10), en colones.
///
/// Reglas:
/// - Las transacciones se procesan en orden cronológico (fecha; el mismo día, compras antes
///   que ventas y luego por hora de registro), no en el orden en que se registraron.
/// - Cada compra recalcula el promedio: (costo acumulado + costo de la compra con comisión)
///   / cantidad en poder. Una venta NO cambia el promedio: saca del costo acumulado
///   cantidad × promedio y deja la diferencia como ganancia realizada.
/// - Los montos en USD se pasan a colones con el FxRate de su propia transacción.
/// - En ningún momento la cantidad en poder puede quedar negativa (RF-07).
/// </summary>
public static class PortfolioCalculator
{
    public static IEnumerable<Transaction> Chronological(IEnumerable<Transaction> transactions) =>
        transactions
            .OrderBy(t => t.Date)
            .ThenBy(t => t.Type == TxType.Buy ? 0 : 1)
            .ThenBy(t => t.CreatedAt);

    /// <summary>Monto de una transacción convertido a colones con su propio tipo de cambio.</summary>
    public static decimal ToCrc(decimal amount, Transaction t) =>
        t.PriceCurrency == Currency.CRC ? amount : amount * t.FxRate;

    /// <summary>Valida los campos de una transacción. Devuelve el mensaje de error o null.</summary>
    public static string? Validate(Transaction t, DateOnly today)
    {
        if (t.Quantity <= 0) return "La cantidad debe ser mayor a cero.";
        if (t.UnitPrice <= 0) return "El precio debe ser mayor a cero.";
        if (t.Fee < 0) return "La comisión no puede ser negativa.";
        if (t.Date > today) return "La fecha no puede ser futura.";
        if (t.PriceCurrency == Currency.USD && t.FxRate <= 0) return "Falta el tipo de cambio de la transacción.";
        if (t.AssetId == AssetIds.Usd && t.PriceCurrency != Currency.CRC) return "Los dólares se compran y venden en colones.";
        return null;
    }

    /// <summary>
    /// Busca la primera venta que deja la cantidad negativa en su fecha. Considera el orden
    /// cronológico, así que también detecta una venta con fecha anterior a la compra.
    /// </summary>
    public static Transaction? FindOversell(IEnumerable<Transaction> transactions)
    {
        foreach (var group in transactions.GroupBy(t => t.AssetId))
        {
            decimal qty = 0;
            foreach (var t in Chronological(group))
            {
                qty += t.Type == TxType.Buy ? t.Quantity : -t.Quantity;
                if (qty < 0) return t;
            }
        }
        return null;
    }

    /// <summary>Cantidad en poder hoy de un activo (compras − ventas).</summary>
    public static decimal QuantityOf(IEnumerable<Transaction> transactions, string assetId) =>
        transactions.Where(t => t.AssetId == assetId).Sum(t => t.Type == TxType.Buy ? t.Quantity : -t.Quantity);

    /// <summary>
    /// Calcula posiciones y totales. <paramref name="prices"/> trae el precio actual en colones
    /// de cada activo; si falta, el activo se valora en cero. <paramref name="fxBuy"/> es el
    /// tipo de cambio de compra de hoy (solo para mostrar totales en USD).
    /// </summary>
    public static PortfolioSummary Calculate(
        IEnumerable<Transaction> transactions,
        IReadOnlyDictionary<string, AssetPrice> prices,
        decimal fxBuy)
    {
        var holdings = new List<Holding>();
        decimal realizedTotal = 0;

        foreach (var group in transactions.GroupBy(t => t.AssetId))
        {
            decimal qty = 0, cost = 0, realized = 0;

            foreach (var t in Chronological(group))
            {
                if (t.Type == TxType.Buy)
                {
                    cost += ToCrc(t.Quantity * t.UnitPrice + t.Fee, t);
                    qty += t.Quantity;
                }
                else
                {
                    if (t.Quantity > qty)
                        throw new InvalidOperationException($"La venta {t.Id} deja la cantidad de {t.AssetId} en negativo.");

                    var avg = cost / qty;
                    var proceeds = ToCrc(t.Quantity * t.UnitPrice - t.Fee, t);
                    realized += proceeds - avg * t.Quantity;
                    cost -= avg * t.Quantity;
                    qty -= t.Quantity;
                    if (qty == 0) cost = 0;
                }
            }

            realizedTotal += realized;
            if (qty <= 0) continue;

            prices.TryGetValue(group.Key, out var price);
            var value = qty * (price?.PriceCrc ?? 0);
            var pnl = value - cost;
            holdings.Add(new Holding(
                group.Key,
                qty,
                AvgCostCrc: Math.Round(cost / qty, 8),
                InvestedCrc: Money(cost),
                ValueCrc: Money(value),
                PnlCrc: Money(pnl),
                PnlPct: Pct(cost == 0 ? 0 : pnl / cost * 100),
                RealizedPnlCrc: Money(realized),
                Change24hPct: price?.Change24hPct ?? 0));
        }

        var totalValue = holdings.Sum(h => h.ValueCrc);
        var totalInvested = holdings.Sum(h => h.InvestedCrc);
        var dayChange = holdings.Sum(DayChange);
        var previous = totalValue - dayChange;

        return new PortfolioSummary(
            ValueCrc: totalValue,
            InvestedCrc: totalInvested,
            PnlCrc: totalValue - totalInvested,
            PnlPct: Pct(totalInvested == 0 ? 0 : (totalValue - totalInvested) / totalInvested * 100),
            RealizedPnlCrc: Money(realizedTotal),
            DayChangeCrc: Money(dayChange),
            DayChangePct: Pct(previous == 0 ? 0 : dayChange / previous * 100),
            FxRate: fxBuy,
            Holdings: holdings.OrderByDescending(h => h.ValueCrc).ToList());
    }

    /// <summary>Montos en colones con 2 decimales; porcentajes con 4.</summary>
    static decimal Money(decimal v) => Math.Round(v, 2);

    static decimal Pct(decimal v) => Math.Round(v, 4);

    /// <summary>Cuánto cambió el valor de la posición en 24 h, a partir de la variación porcentual.</summary>
    static decimal DayChange(Holding h)
    {
        var factor = 1 + h.Change24hPct / 100;
        return factor <= 0 ? 0 : h.ValueCrc - h.ValueCrc / factor;
    }
}
