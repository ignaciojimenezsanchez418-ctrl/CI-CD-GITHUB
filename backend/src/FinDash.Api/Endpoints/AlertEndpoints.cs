using FinDash.Api.Auth;
using FinDash.Api.Data;
using FinDash.Core.Models;
using FinDash.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace FinDash.Api.Endpoints;

public sealed record AlertInput(string AssetId, AlertCondition Condition, decimal Threshold, Currency Currency, int? CooldownMinutes);

public sealed record AlertDto(
    Guid Id, string AssetId, AlertCondition Condition, decimal Threshold, Currency Currency,
    bool IsEnabled, AlertState State, int CooldownMinutes, DateTimeOffset? LastTriggeredAt);

public sealed record EnabledInput(bool IsEnabled);

public sealed record AlertEventDto(
    Guid Id, Guid? AlertId, string AssetId, AlertCondition Condition, decimal Threshold, Currency Currency,
    decimal PriceAtTrigger, DateTimeOffset TriggeredAt, DateTimeOffset? DeliveredAt);

public sealed record DeliveredInput(IReadOnlyList<Guid> Ids);

/// <summary>Alertas de precio y de tipo de cambio (RF-10, RF-11).</summary>
public static class AlertEndpoints
{
    const int MaxCooldownMinutes = 7 * 24 * 60;

    public static void MapAlerts(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/alerts").WithTags("Alertas").RequireAuthorization();

        group.MapGet("/", async (HttpContext http, FinDashDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var list = await db.PriceAlerts.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct);
            return list.OrderBy(a => a.CreatedAt).Select(ToDto).ToList();
        })
        .WithSummary("Alertas del usuario");

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, FinDashDbContext db, CancellationToken ct) =>
        {
            var alert = await Find(db, id, http.User.UserId(), ct);
            return alert is null ? Errors.NotFound("Esa alerta no existe.") : Results.Ok(ToDto(alert));
        })
        .WithSummary("Una alerta");

        group.MapPost("/", async (AlertInput body, HttpContext http, FinDashDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (await Validate(body, db, ct) is { } error) return error;

            var alert = new PriceAlert
            {
                UserId = http.User.UserId(),
                AssetId = body.AssetId,
                CreatedAt = time.GetUtcNow(),
            };
            Apply(alert, body);
            db.PriceAlerts.Add(alert);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/alerts/{alert.Id}", ToDto(alert));
        })
        .WithSummary("Crea una alerta");

        group.MapPut("/{id:guid}", async (Guid id, AlertInput body, HttpContext http, FinDashDbContext db, CancellationToken ct) =>
        {
            var alert = await Find(db, id, http.User.UserId(), ct);
            if (alert is null) return Errors.NotFound("Esa alerta no existe.");
            if (await Validate(body, db, ct) is { } error) return error;

            // Editar no cambia si está encendida o apagada; sí la rearma con el nuevo umbral.
            Apply(alert, body);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(alert));
        })
        .WithSummary("Edita una alerta");

        group.MapPut("/{id:guid}/enabled", async (Guid id, EnabledInput body, HttpContext http, FinDashDbContext db, CancellationToken ct) =>
        {
            var alert = await Find(db, id, http.User.UserId(), ct);
            if (alert is null) return Errors.NotFound("Esa alerta no existe.");
            alert.IsEnabled = body.IsEnabled;
            if (body.IsEnabled) alert.State = AlertState.Armed;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(alert));
        })
        .WithSummary("Enciende o apaga una alerta");

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, FinDashDbContext db, CancellationToken ct) =>
        {
            var alert = await Find(db, id, http.User.UserId(), ct);
            if (alert is null) return Errors.NotFound("Esa alerta no existe.");
            db.PriceAlerts.Remove(alert);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithSummary("Borra una alerta (su historial de disparos se conserva)");

        group.MapGet("/events", async (bool? pending, HttpContext http, FinDashDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var query = db.AlertEvents.AsNoTracking().Where(e => e.UserId == userId);
            if (pending == true) query = query.Where(e => e.DeliveredAt == null);
            var list = await query.OrderByDescending(e => e.TriggeredAt).Take(100).ToListAsync(ct);
            return list.Select(ToDto).ToList();
        })
        .WithSummary("Alertas disparadas. pending=true devuelve solo las que la app no ha notificado");

        group.MapPost("/events/delivered", async (DeliveredInput body, HttpContext http, FinDashDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var ids = body.Ids ?? [];
            var events = await db.AlertEvents
                .Where(e => e.UserId == userId && ids.Contains(e.Id) && e.DeliveredAt == null)
                .ToListAsync(ct);
            var now = time.GetUtcNow();
            foreach (var e in events) e.DeliveredAt = now;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithSummary("Marca eventos como notificados para no repetirlos (RF-11)");
    }

    static Task<PriceAlert?> Find(FinDashDbContext db, Guid id, Guid userId, CancellationToken ct) =>
        db.PriceAlerts.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);

    static async Task<IResult?> Validate(AlertInput body, FinDashDbContext db, CancellationToken ct)
    {
        if (!await db.Assets.AnyAsync(a => a.Id == body.AssetId, ct)) return Errors.NotFound("Ese activo no existe.");
        if (body.Threshold <= 0) return Errors.BadRequest("El monto debe ser mayor a cero.");
        if (body.AssetId == AssetIds.Usd && body.Currency != Currency.CRC)
            return Errors.BadRequest("Las alertas del dólar se definen en colones.");
        if (body.CooldownMinutes is < 0 or > MaxCooldownMinutes)
            return Errors.BadRequest("El tiempo entre avisos debe estar entre 0 minutos y 7 días.");
        return null;
    }

    static void Apply(PriceAlert alert, AlertInput body)
    {
        alert.AssetId = body.AssetId;
        alert.Condition = body.Condition;
        alert.Threshold = body.Threshold;
        alert.Currency = body.Currency;
        alert.CooldownMinutes = body.CooldownMinutes ?? AlertRuleEngine.DefaultCooldownMinutes;
        alert.State = AlertState.Armed;
    }

    static AlertDto ToDto(PriceAlert a) =>
        new(a.Id, a.AssetId, a.Condition, a.Threshold, a.Currency, a.IsEnabled, a.State, a.CooldownMinutes, a.LastTriggeredAt);

    static AlertEventDto ToDto(AlertEvent e) =>
        new(e.Id, e.AlertId, e.AssetId, e.Condition, e.Threshold, e.Currency, e.PriceAtTrigger, e.TriggeredAt, e.DeliveredAt);
}
