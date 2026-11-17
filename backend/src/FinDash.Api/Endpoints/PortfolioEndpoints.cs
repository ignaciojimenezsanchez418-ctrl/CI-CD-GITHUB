using FinDash.Api.Auth;
using FinDash.Api.Data;
using FinDash.Api.Prices;
using FinDash.Core.Models;
using FinDash.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace FinDash.Api.Endpoints;

/// <summary>
/// Nueva transacción. FxRate es opcional: si el usuario no sabe a qué tipo de cambio compró,
/// el servidor usa el oficial de compra de esa fecha.
/// </summary>
public sealed record NewTransaction(
    string AssetId,
    TxType Type,
    decimal Quantity,
    decimal UnitPrice,
    Currency PriceCurrency,
    decimal Fee,
    DateOnly Date,
    decimal? FxRate);

public sealed record TransactionDto(
    Guid Id, string AssetId, TxType Type, decimal Quantity, decimal UnitPrice,
    Currency PriceCurrency, decimal Fee, DateOnly Date, decimal FxRate, DateTimeOffset CreatedAt);

public sealed record QuantityDto(string AssetId, decimal Quantity);

/// <summary>Transacciones y portafolio (RF-06 a RF-08).</summary>
public static class PortfolioEndpoints
{
    public static void MapPortfolio(this IEndpointRouteBuilder app)
    {
        app.MapGet("/portfolio", async (HttpContext http, FinDashDbContext db, QuoteStore store, CancellationToken ct) =>
        {
            var market = store.Current;
            if (market.Fx is null) return Errors.Unavailable("Todavía no hay tipo de cambio para valorar el portafolio.");

            var txs = await UserTransactions(db, http.User.UserId(), ct);
            return Results.Ok(PortfolioCalculator.Calculate(txs, market.PricesCrc(), market.Fx.Buy));
        })
        .WithTags("Portafolio")
        .RequireAuthorization()
        .WithSummary("Patrimonio, costo promedio y ganancia o pérdida en CRC (y USD) (RF-08)");

        app.MapGet("/portfolio/quantity/{assetId}", async (string assetId, HttpContext http, FinDashDbContext db, CancellationToken ct) =>
        {
            var txs = await UserTransactions(db, http.User.UserId(), ct);
            return Results.Ok(new QuantityDto(assetId, PortfolioCalculator.QuantityOf(txs, assetId)));
        })
        .WithTags("Portafolio")
        .RequireAuthorization()
        .WithSummary("Cantidad en poder de un activo (para el formulario de venta)");

        var group = app.MapGroup("/transactions").WithTags("Portafolio").RequireAuthorization();

        group.MapGet("/", async (HttpContext http, FinDashDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var list = await db.Transactions.AsNoTracking().Where(t => t.UserId == userId).ToListAsync(ct);
            return list.OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAt).Select(ToDto).ToList();
        })
        .WithSummary("Movimientos del usuario, del más reciente al más viejo");

        group.MapPost("/", async (NewTransaction body, HttpContext http, FinDashDbContext db, FxRateLookup fxLookup,
            TimeProvider time, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            if (!await db.Assets.AnyAsync(a => a.Id == body.AssetId, ct)) return Errors.NotFound("Ese activo no existe.");

            var fxRate = 1m;
            if (body.PriceCurrency == Currency.USD)
            {
                fxRate = body.FxRate is > 0 ? body.FxRate.Value : await fxLookup.GetBuyRateAsync(body.Date, ct) ?? 0;
                if (fxRate <= 0) return Errors.Unavailable("No hay tipo de cambio para esa fecha. Escríbalo en el formulario.");
            }

            var entity = new TransactionEntity
            {
                UserId = userId,
                AssetId = body.AssetId,
                Type = body.Type,
                Quantity = body.Quantity,
                UnitPrice = body.UnitPrice,
                PriceCurrency = body.PriceCurrency,
                Fee = body.Fee,
                Date = body.Date,
                FxRate = fxRate,
                CreatedAt = time.GetUtcNow(),
            };

            var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(1); // margen por zona horaria
            if (PortfolioCalculator.Validate(entity.ToModel(), today) is { } error) return Errors.BadRequest(error);

            // RF-07: con la nueva transacción, ninguna venta puede dejar la cantidad negativa
            // en su fecha (también atrapa una venta con fecha anterior a la compra).
            var existing = await UserTransactions(db, userId, ct);
            if (PortfolioCalculator.FindOversell(existing.Append(entity.ToModel())) is not null)
                return Errors.BadRequest("No se puede vender más de lo que se tiene en esa fecha.");

            db.Transactions.Add(entity);
            if (!await db.WatchlistItems.AnyAsync(w => w.UserId == userId && w.AssetId == entity.AssetId, ct))
                db.WatchlistItems.Add(new WatchlistItem { UserId = userId, AssetId = entity.AssetId, AddedAt = entity.CreatedAt });
            await db.SaveChangesAsync(ct);

            return Results.Created($"/transactions/{entity.Id}", ToDto(entity));
        })
        .WithSummary("Registra una compra o venta, con validaciones (RF-06, RF-07)");

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, FinDashDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var entity = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);
            if (entity is null) return Errors.NotFound("Ese movimiento no existe.");

            var remaining = (await UserTransactions(db, userId, ct)).Where(t => t.Id != id);
            if (PortfolioCalculator.FindOversell(remaining) is not null)
                return Errors.BadRequest("No se puede borrar esa compra: una venta posterior quedaría sin saldo.");

            db.Transactions.Remove(entity);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithSummary("Borra un movimiento si no deja ventas sin saldo");
    }

    static async Task<List<Transaction>> UserTransactions(FinDashDbContext db, Guid userId, CancellationToken ct) =>
        (await db.Transactions.AsNoTracking().Where(t => t.UserId == userId).ToListAsync(ct))
            .Select(t => t.ToModel())
            .ToList();

    static TransactionDto ToDto(TransactionEntity t) =>
        new(t.Id, t.AssetId, t.Type, t.Quantity, t.UnitPrice, t.PriceCurrency, t.Fee, t.Date, t.FxRate, t.CreatedAt);
}
