using FinDash.Api.Auth;
using FinDash.Api.Data;
using FinDash.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace FinDash.Api.Endpoints;

public sealed record MeDto(string Email, string? DisplayName, Currency BaseCurrency);

public sealed record MeInput(string? DisplayName, Currency? BaseCurrency);

/// <summary>Datos y preferencias del usuario guardadas en el servidor (RF-12).</summary>
public static class MeEndpoints
{
    public static void MapMe(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/me").WithTags("Usuario").RequireAuthorization();

        group.MapGet("/", async (HttpContext http, FinDashDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FindAsync([http.User.UserId()], ct);
            return user is null ? Errors.Unauthorized("La sesión ya no es válida.") : Results.Ok(ToDto(user));
        })
        .WithSummary("Datos del usuario de la sesión");

        group.MapPut("/", async (MeInput body, HttpContext http, FinDashDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FindAsync([http.User.UserId()], ct);
            if (user is null) return Errors.Unauthorized("La sesión ya no es válida.");
            if (body.DisplayName is { } name) user.DisplayName = name.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 100)] : null;
            if (body.BaseCurrency is { } currency) user.BaseCurrency = currency;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(user));
        })
        .WithSummary("Cambia el nombre o la moneda base");
    }

    static MeDto ToDto(User u) => new(u.Email, u.DisplayName, u.BaseCurrency);
}
