using FinDash.Api.Auth;
using FinDash.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FinDash.Api.Endpoints;

public sealed record Credentials(string Email, string Password);

/// <summary>Registro e inicio de sesión (RF-01, US-107).</summary>
public static class AuthEndpoints
{
    public const int MinPasswordLength = 8;

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth").RequireRateLimiting(RateLimits.Auth);

        group.MapPost("/register", async (Credentials body, FinDashDbContext db, IPasswordHasher<User> hasher,
            JwtTokenService tokens, TimeProvider time, CancellationToken ct) =>
        {
            var email = Normalize(body.Email);
            if (!IsValidEmail(email)) return Errors.BadRequest("El correo no parece válido.");
            if ((body.Password ?? "").Length < MinPasswordLength)
                return Errors.BadRequest($"La contraseña necesita al menos {MinPasswordLength} caracteres.");
            if (await db.Users.AnyAsync(u => u.Email == email, ct))
                return Errors.Conflict("Ya hay una cuenta con ese correo.");

            var user = new User { Email = email, CreatedAt = time.GetUtcNow() };
            user.PasswordHash = hasher.HashPassword(user, body.Password!);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);

            return Results.Ok(tokens.Create(user));
        })
        .WithSummary("Crea una cuenta y devuelve el token de sesión");

        group.MapPost("/login", async (Credentials body, FinDashDbContext db, IPasswordHasher<User> hasher,
            JwtTokenService tokens, CancellationToken ct) =>
        {
            var email = Normalize(body.Email);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

            // Mismo mensaje si el correo no existe o la contraseña es incorrecta.
            if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, body.Password ?? "") == PasswordVerificationResult.Failed)
                return Errors.Unauthorized("Correo o contraseña incorrectos.");

            return Results.Ok(tokens.Create(user));
        })
        .WithSummary("Inicia sesión y devuelve el token de sesión");
    }

    static string Normalize(string? email) => (email ?? "").Trim().ToLowerInvariant();

    static bool IsValidEmail(string email)
    {
        var at = email.IndexOf('@');
        return email.Length <= 256 && at > 0 && email.LastIndexOf('.') > at + 1 && !email.EndsWith('.') && !email.Contains(' ');
    }
}
