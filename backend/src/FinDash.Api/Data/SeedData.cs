using FinDash.Core.Models;

namespace FinDash.Api.Data;

/// <summary>
/// Catálogo inicial de activos. La búsqueda (US-204) se hace sobre este catálogo para no
/// gastar llamadas de CoinGecko; agregar un activo es agregar una fila aquí y una migración.
/// </summary>
public static class SeedData
{
    public static readonly Asset[] Assets =
    [
        Fiat(AssetIds.Usd, "USD", "Dólar"),
        Crypto("bitcoin", "BTC", "Bitcoin"),
        Crypto("ethereum", "ETH", "Ethereum"),
        Crypto("tether", "USDT", "Tether"),
        Crypto("usd-coin", "USDC", "USD Coin"),
        Crypto("binancecoin", "BNB", "BNB"),
        Crypto("solana", "SOL", "Solana"),
        Crypto("ripple", "XRP", "XRP"),
        Crypto("cardano", "ADA", "Cardano"),
        Crypto("dogecoin", "DOGE", "Dogecoin"),
        Crypto("tron", "TRX", "TRON"),
        Crypto("avalanche-2", "AVAX", "Avalanche"),
        Crypto("chainlink", "LINK", "Chainlink"),
        Crypto("polkadot", "DOT", "Polkadot"),
        Crypto("litecoin", "LTC", "Litecoin"),
        Crypto("bitcoin-cash", "BCH", "Bitcoin Cash"),
        Crypto("stellar", "XLM", "Stellar"),
    ];

    static Asset Fiat(string id, string symbol, string name) =>
        new() { Id = id, Symbol = symbol, Name = name, Kind = AssetKind.Fiat, Source = "BCCR" };

    static Asset Crypto(string id, string symbol, string name) =>
        new() { Id = id, Symbol = symbol, Name = name, Kind = AssetKind.Crypto, Source = "CoinGecko" };
}
