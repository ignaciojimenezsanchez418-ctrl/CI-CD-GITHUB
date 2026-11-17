namespace FinDash.Core.Models;

public enum Currency { CRC, USD }

public enum AssetKind { Fiat, Crypto }

public static class AssetIds
{
    /// <summary>Id del dólar (tipo de cambio USD/CRC). Las cripto usan el id de CoinGecko.</summary>
    public const string Usd = "usd";
}

public enum ChartRange { Day, Week, Month, Year }

/// <summary>Un punto de la gráfica histórica.</summary>
public sealed record PricePoint(DateTimeOffset Time, decimal PriceCrc, decimal PriceUsd);
