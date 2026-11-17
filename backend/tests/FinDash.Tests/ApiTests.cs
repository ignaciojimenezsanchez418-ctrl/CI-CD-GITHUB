using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FinDash.Api.Alerts;
using FinDash.Api.Auth;
using FinDash.Api.Endpoints;
using FinDash.Api.Prices;
using FinDash.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FinDash.Tests;

/// <summary>API con base SQLite temporal y proveedores falsos; los workers se llaman a mano.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"findash-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_dbPath}");
        builder.UseSetting("Jwt:Key", "clave-solo-para-pruebas-de-integracion-0123456789");
        builder.UseSetting("Providers:Mode", "Fake");
        builder.UseSetting("Workers:Enabled", "false");
        builder.UseSetting("RateLimiting:AuthPerMinute", "1000");
    }

    /// <summary>Lo que haría PriceService: trae tipo de cambio y precios a la caché.</summary>
    public async Task RefreshPricesAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var refresher = scope.ServiceProvider.GetRequiredService<PriceRefresher>();
        Assert.True(await refresher.RefreshFxAsync(default));
        Assert.True(await refresher.RefreshCryptoAsync(default));
    }

    /// <summary>Lo que haría AlertWorker en su siguiente minuto.</summary>
    public async Task<int> EvaluateAlertsAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AlertEvaluator>().EvaluateAsync(default);
    }

    public QuoteStore Quotes => Services.GetRequiredService<QuoteStore>();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}

public class ApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    async Task<HttpClient> SignedInClientAsync(string? email = null)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register",
            new Credentials(email ?? $"u{Guid.NewGuid():N}@correo.com", "clave-segura-1"));
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);
        return client;
    }

    static async Task<string?> DetailAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString();

    [Theory]
    [InlineData("/portfolio")]
    [InlineData("/transactions")]
    [InlineData("/alerts")]
    [InlineData("/market")]
    [InlineData("/me")]
    public async Task SinToken_Responde401(string url)
    {
        var response = await factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RegistroEInicioDeSesion()
    {
        var client = factory.CreateClient();
        var email = $"ana{Guid.NewGuid():N}@correo.com";

        var weak = await client.PostAsJsonAsync("/auth/register", new Credentials(email, "corta"));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        Assert.True((await client.PostAsJsonAsync("/auth/register", new Credentials(email, "clave-segura-1"))).IsSuccessStatusCode);

        var duplicate = await client.PostAsJsonAsync("/auth/register", new Credentials(email.ToUpperInvariant(), "clave-segura-1"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var wrong = await client.PostAsJsonAsync("/auth/login", new Credentials(email, "otra-clave"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("Correo o contraseña incorrectos.", await DetailAsync(wrong));

        var ok = await client.PostAsJsonAsync("/auth/login", new Credentials($"  {email} ", "clave-segura-1"));
        var auth = await ok.Content.ReadFromJsonAsync<AuthResponse>(Json);
        Assert.False(string.IsNullOrEmpty(auth!.Token));

        client.DefaultRequestHeaders.Authorization = new("Bearer", auth.Token);
        var me = await client.GetFromJsonAsync<MeDto>("/me", Json);
        Assert.Equal(email, me!.Email);
        Assert.Equal(Currency.CRC, me.BaseCurrency);
    }

    [Fact]
    public async Task Busqueda_Bit_DevuelveBitcoin()
    {
        var client = await SignedInClientAsync();
        var results = await client.GetFromJsonAsync<List<AssetDto>>("/assets/search?q=bit", Json);
        Assert.Contains(results!, a => a.Id == "bitcoin");
    }

    [Fact]
    public async Task Watchlist_AgregarYQuitar()
    {
        var client = await SignedInClientAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync("/watchlist/solana", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync("/watchlist/solana", null)).StatusCode); // idempotente
        Assert.Equal(["solana"], await client.GetFromJsonAsync<List<string>>("/watchlist"));

        await client.DeleteAsync("/watchlist/solana");
        Assert.Empty((await client.GetFromJsonAsync<List<string>>("/watchlist"))!);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync("/watchlist/no-existe", null)).StatusCode);
    }

    [Fact]
    public async Task Mercado_DevuelvePreciosDeLaCache()
    {
        await factory.RefreshPricesAsync();
        var client = await SignedInClientAsync();

        var market = await client.GetFromJsonAsync<MarketDto>("/market", Json);

        Assert.NotNull(market!.Fx);
        Assert.False(market.IsStale);
        Assert.Equal(market.Fx!.Buy, market.Quotes["usd"].PriceCrc);
        Assert.Equal(market.Quotes["bitcoin"].PriceUsd * market.Fx.Buy, market.Quotes["bitcoin"].PriceCrc);

        var history = await client.GetFromJsonAsync<List<PricePoint>>("/assets/bitcoin/history?range=Week", Json);
        Assert.NotEmpty(history!);
    }

    [Fact]
    public async Task Transacciones_VentaExcedidaSeRechaza_YElPortafolioSeCalcula()
    {
        await factory.RefreshPricesAsync();
        var client = await SignedInClientAsync();
        var day = new DateOnly(2026, 10, 1);

        var buy = await client.PostAsJsonAsync("/transactions",
            new NewTransaction("solana", TxType.Buy, 10, 90, Currency.USD, 1, day, FxRate: 510), Json);
        Assert.Equal(HttpStatusCode.Created, buy.StatusCode);

        // M3: vender más de lo que se tiene.
        var oversell = await client.PostAsJsonAsync("/transactions",
            new NewTransaction("solana", TxType.Sell, 11, 100, Currency.USD, 0, day.AddDays(1), FxRate: 510), Json);
        Assert.Equal(HttpStatusCode.BadRequest, oversell.StatusCode);
        Assert.Equal("No se puede vender más de lo que se tiene en esa fecha.", await DetailAsync(oversell));

        // Venta con fecha anterior a la compra.
        var early = await client.PostAsJsonAsync("/transactions",
            new NewTransaction("solana", TxType.Sell, 1, 100, Currency.USD, 0, day.AddDays(-1), FxRate: 510), Json);
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);

        // Sin FxRate: el servidor pone el tipo de cambio de esa fecha.
        var sell = await client.PostAsJsonAsync("/transactions",
            new NewTransaction("solana", TxType.Sell, 4, 100, Currency.USD, 0, day.AddDays(2), FxRate: null), Json);
        Assert.Equal(HttpStatusCode.Created, sell.StatusCode);
        Assert.True((await sell.Content.ReadFromJsonAsync<TransactionDto>(Json))!.FxRate > 0);

        var quantity = await client.GetFromJsonAsync<QuantityDto>("/portfolio/quantity/solana", Json);
        Assert.Equal(6m, quantity!.Quantity);

        var summary = await client.GetFromJsonAsync<PortfolioSummary>("/portfolio", Json);
        var h = Assert.Single(summary!.Holdings);
        Assert.Equal("solana", h.AssetId);
        Assert.Equal((10 * 90 + 1) * 510m / 10, h.AvgCostCrc);

        // La compra deja el activo en la watchlist.
        Assert.Contains("solana", (await client.GetFromJsonAsync<List<string>>("/watchlist"))!);

        // No se puede borrar la compra: la venta quedaría sin saldo.
        var buyId = (await buy.Content.ReadFromJsonAsync<TransactionDto>(Json))!.Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/transactions/{buyId}")).StatusCode);
    }

    [Fact]
    public async Task CadaUsuarioVeSoloSusDatos()
    {
        var ana = await SignedInClientAsync();
        var beto = await SignedInClientAsync();

        var tx = await ana.PostAsJsonAsync("/transactions",
            new NewTransaction("usd", TxType.Buy, 100, 510, Currency.CRC, 0, new DateOnly(2026, 10, 1), null), Json);
        var alert = await ana.PostAsJsonAsync("/alerts",
            new AlertInput("usd", AlertCondition.Below, 500, Currency.CRC, null), Json);
        var alertId = (await alert.Content.ReadFromJsonAsync<AlertDto>(Json))!.Id;
        var txId = (await tx.Content.ReadFromJsonAsync<TransactionDto>(Json))!.Id;

        Assert.Empty((await beto.GetFromJsonAsync<List<TransactionDto>>("/transactions", Json))!);
        Assert.Empty((await beto.GetFromJsonAsync<List<AlertDto>>("/alerts", Json))!);
        Assert.Equal(HttpStatusCode.NotFound, (await beto.GetAsync($"/alerts/{alertId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await beto.DeleteAsync($"/alerts/{alertId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await beto.DeleteAsync($"/transactions/{txId}")).StatusCode);
        Assert.Single((await ana.GetFromJsonAsync<List<AlertDto>>("/alerts", Json))!);
    }

    [Fact]
    public async Task Alertas_SeDisparanUnaVez_YSeMarcanEntregadas()
    {
        await factory.RefreshPricesAsync();
        var client = await SignedInClientAsync();
        var fxBuy = factory.Quotes.Current.Fx!.Buy;

        // M4: "dólar baja de X" con X un poco ARRIBA del tipo de cambio actual → se cumple ya.
        var created = await client.PostAsJsonAsync("/alerts",
            new AlertInput("usd", AlertCondition.Below, fxBuy + 1, Currency.CRC, 60), Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var alert = (await created.Content.ReadFromJsonAsync<AlertDto>(Json))!;

        // Una alerta que no se cumple.
        await client.PostAsJsonAsync("/alerts", new AlertInput("bitcoin", AlertCondition.Above, 10_000_000, Currency.USD, null), Json);

        await factory.EvaluateAlertsAsync();
        await factory.EvaluateAlertsAsync(); // el siguiente minuto no la repite

        var pending = await client.GetFromJsonAsync<List<AlertEventDto>>("/alerts/events?pending=true", Json);
        var ev = Assert.Single(pending!);
        Assert.Equal(alert.Id, ev.AlertId);
        Assert.Equal(fxBuy, ev.PriceAtTrigger);

        var refreshed = await client.GetFromJsonAsync<AlertDto>($"/alerts/{alert.Id}", Json);
        Assert.Equal(AlertState.Triggered, refreshed!.State);

        // La app notifica y marca como entregada: ya no aparece como pendiente.
        await client.PostAsJsonAsync("/alerts/events/delivered", new DeliveredInput([ev.Id]), Json);
        Assert.Empty((await client.GetFromJsonAsync<List<AlertEventDto>>("/alerts/events?pending=true", Json))!);
        Assert.Single((await client.GetFromJsonAsync<List<AlertEventDto>>("/alerts/events", Json))!);

        // Apagar la alerta y borrarla: el historial se conserva.
        var off = await client.PutAsJsonAsync($"/alerts/{alert.Id}/enabled", new EnabledInput(false), Json);
        Assert.False((await off.Content.ReadFromJsonAsync<AlertDto>(Json))!.IsEnabled);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/alerts/{alert.Id}")).StatusCode);
        var history = await client.GetFromJsonAsync<List<AlertEventDto>>("/alerts/events", Json);
        Assert.Null(Assert.Single(history!).AlertId);
    }

    [Fact]
    public async Task Alertas_Validaciones()
    {
        var client = await SignedInClientAsync();

        var zero = await client.PostAsJsonAsync("/alerts", new AlertInput("bitcoin", AlertCondition.Above, 0, Currency.USD, null), Json);
        Assert.Equal("El monto debe ser mayor a cero.", await DetailAsync(zero));

        var usdInUsd = await client.PostAsJsonAsync("/alerts", new AlertInput("usd", AlertCondition.Above, 520, Currency.USD, null), Json);
        Assert.Equal(HttpStatusCode.BadRequest, usdInUsd.StatusCode);

        var unknown = await client.PostAsJsonAsync("/alerts", new AlertInput("no-existe", AlertCondition.Above, 1, Currency.USD, null), Json);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
