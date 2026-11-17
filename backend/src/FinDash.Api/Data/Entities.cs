using FinDash.Core.Models;

namespace FinDash.Api.Data;

// Las 7 tablas de la sección 6 del documento. Diferencias con el diagrama:
// - Assets usa como Id el id externo ("bitcoin", "usd"): es lo que la app ya usa.
// - Transactions guarda FxRate (tipo de cambio del día de la transacción).
// - PriceAlerts tiene IsEnabled (encendida/apagada por el usuario) además de State.
// - AlertEvents copia activo, condición y umbral para que el historial no cambie si se edita
//   o borra la alerta, y guarda UserId para consultarlos directo.
// - PriceSnapshots guarda SellPrice para el dólar (tipo de cambio de venta).

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public Currency BaseCurrency { get; set; } = Currency.CRC;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Asset
{
    public required string Id { get; set; }
    public required string Symbol { get; set; }
    public required string Name { get; set; }
    public AssetKind Kind { get; set; }

    /// <summary>"CoinGecko" o "BCCR".</summary>
    public required string Source { get; set; }
}

public sealed class WatchlistItem
{
    public Guid UserId { get; set; }
    public required string AssetId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

public sealed class TransactionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string AssetId { get; set; }
    public TxType Type { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public Currency PriceCurrency { get; set; }
    public decimal Fee { get; set; }
    public DateOnly Date { get; set; }
    public decimal FxRate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Transaction ToModel() => new()
    {
        Id = Id,
        AssetId = AssetId,
        Type = Type,
        Quantity = Quantity,
        UnitPrice = UnitPrice,
        PriceCurrency = PriceCurrency,
        Fee = Fee,
        Date = Date,
        FxRate = FxRate,
        CreatedAt = CreatedAt,
    };
}

public sealed class PriceAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string AssetId { get; set; }
    public AlertCondition Condition { get; set; }
    public decimal Threshold { get; set; }
    public Currency Currency { get; set; }
    public bool IsEnabled { get; set; } = true;
    public AlertState State { get; set; } = AlertState.Armed;
    public int CooldownMinutes { get; set; }
    public DateTimeOffset? LastTriggeredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AlertEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Null si la alerta se borró después; el evento queda en el historial.</summary>
    public Guid? AlertId { get; set; }

    public Guid UserId { get; set; }
    public required string AssetId { get; set; }
    public AlertCondition Condition { get; set; }
    public decimal Threshold { get; set; }
    public Currency Currency { get; set; }
    public decimal PriceAtTrigger { get; set; }
    public DateTimeOffset TriggeredAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

public sealed class PriceSnapshot
{
    public long Id { get; set; }
    public required string AssetId { get; set; }

    /// <summary>Cripto: precio en USD. Dólar: tipo de cambio de compra en CRC.</summary>
    public decimal Price { get; set; }

    public Currency Currency { get; set; }

    /// <summary>Solo para el dólar: tipo de cambio de venta.</summary>
    public decimal? SellPrice { get; set; }

    public decimal Change24hPct { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
