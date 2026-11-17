using FinDash.Api.Auth;
using FinDash.Api.Data;
using FinDash.Api.Prices;
using FinDash.Api.Providers;
using FinDash.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace FinDash.Api.Endpoints;

public sealed record AssetDto(string Id, string Symbol, string Name, AssetKind Kind);

public sealed record FxDto(decimal Buy, decimal Sell, decimal Change24hPct, DateOnly Date);

/// <summary>Lo mismo que MarketSnapshot de la app. IsStale = el último refresco falló y son datos de caché.</summary>
public sealed record MarketDto(FxDto? Fx, IReadOnlyDictionary<string, MarketQuote> Quotes, DateTimeOffset? UpdatedAt, bool IsStale);

/// <summary>Catálogo, precios, historial y watchlist (RF-02 a RF-04, RF-09).</summary>
public static class MarketEndpoints
{
    public static void MapMarket(this IEndpointRouteBuilder app)
    {
        var assets = app.MapGroup("/assets").WithTags("Mercado").RequireAuthorization();

        assets.MapGet("/", async (FinDashDbContext db, CancellationToken ct) =>
            await db.Assets.AsNoTracking().OrderBy(a => a.Kind).ThenBy(a => a.Name).Select(a => ToDto(a)).ToListAsync(ct))
            .WithSummary("Catálogo de activos");

        assets.MapGet("/search", async (string? q, FinDashDbContext db, CancellationToken ct) =>
        {
            var all = await db.Assets.AsNoTracking().ToListAsync(ct);
            var query = (q ?? "").Trim();
            return all
                .Where(a => query.Length == 0
                    || a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || a.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || a.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Kind).ThenBy(a => a.Name)
                .Select(ToDto)
                .ToList();
        })
        .WithSummary("Busca activos por nombre, símbolo o id (US-204)");

        assets.MapGet("/{id}/history", async (string id, ChartRange? range, FinDashDbContext db, HistoryService history, CancellationToken ct) =>
        {
            if (!await db.Assets.AnyAsync(a => a.Id == id, ct)) return Errors.NotFound("Ese activo no existe.");
            try
            {
                var points = await history.GetAsync(id, range ?? ChartRange.Day, ct);
                return points is null
                    ? Errors.Unavailable("Todavía no hay tipo de cambio para calcular la gráfica.")
                    : Results.Ok(points);
            }
            catch (Exception ex) when (ex is RateLimitedException or HttpRequestException)
            {
                return Errors.Unavailable("La fuente de precios no respondió. Intente de nuevo en un minuto.");
            }
        })
        .WithSummary("Historial para la gráfica: range = Day, Week, Month o Year (RF-09)");

        app.MapGet("/market", (QuoteStore store) =>
        {
            var s = store.Current;
            var quotes = new Dictionary<string, MarketQuote>();
            if (s.Fx is not null)
            {
                quotes[AssetIds.Usd] = s.Quote(AssetIds.Usd)!;
                foreach (var id in s.Crypto.Keys) quotes[id] = s.Quote(id)!;
            }
            var updated = new[] { s.CryptoUpdatedAt, s.FxUpdatedAt }.Where(d => d is not null).Max();
            var fx = s.Fx is null ? null : new FxDto(s.Fx.Buy, s.Fx.Sell, s.Fx.Change24hPct, s.Fx.Date);
            return Results.Ok(new MarketDto(fx, quotes, updated, s.LastRefreshFailed));
        })
        .WithTags("Mercado")
        .RequireAuthorization()
        .WithSummary("Precios actuales y tipo de cambio desde la caché del servidor (RF-03, RF-04)");

        var watchlist = app.MapGroup("/watchlist").WithTags("Watchlist").RequireAuthorization();

        watchlist.MapGet("/", async (HttpContext http, FinDashDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            return await db.WatchlistItems.Where(w => w.UserId == userId)
                .OrderBy(w => w.AddedAt).Select(w => w.AssetId).ToListAsync(ct);
        })
        .WithSummary("Ids de los activos que sigue el usuario");

        watchlist.MapPut("/{assetId}", async (string assetId, HttpContext http, FinDashDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            if (!await db.Assets.AnyAsync(a => a.Id == assetId, ct)) return Errors.NotFound("Ese activo no existe.");
            if (!await db.WatchlistItems.AnyAsync(w => w.UserId == userId && w.AssetId == assetId, ct))
            {
                db.WatchlistItems.Add(new WatchlistItem { UserId = userId, AssetId = assetId, AddedAt = time.GetUtcNow() });
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        })
        .WithSummary("Sigue un activo");

        watchlist.MapDelete("/{assetId}", async (string assetId, HttpContext http, FinDashDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            await db.WatchlistItems.Where(w => w.UserId == userId && w.AssetId == assetId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        })
        .WithSummary("Deja de seguir un activo");
    }

    static AssetDto ToDto(Asset a) => new(a.Id, a.Symbol, a.Name, a.Kind);
}
