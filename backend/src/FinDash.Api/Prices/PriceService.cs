using Microsoft.Extensions.Options;

namespace FinDash.Api.Prices;

public sealed class PriceOptions
{
    /// <summary>
    /// Cada cuánto se piden precios cripto. 5 min por defecto: el plan Demo de CoinGecko tiene
    /// además un tope mensual de llamadas, y cada 30–60 s se pasaría de ese tope.
    /// </summary>
    public int CryptoRefreshMinutes { get; set; } = 5;

    /// <summary>El BCCR publica el tipo de cambio una vez al día.</summary>
    public int FxRefreshHours { get; set; } = 6;
}

/// <summary>
/// Servicio en segundo plano (D3) que mantiene la caché de precios al día para todos los
/// usuarios, sin importar cuántos haya (RNF-02).
/// </summary>
public sealed class PriceService(
    IServiceScopeFactory scopes,
    IOptions<PriceOptions> options,
    IOptions<WorkerOptions> workers,
    TimeProvider time,
    ILogger<PriceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!workers.Value.Enabled) return;

        await using (var scope = scopes.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<PriceRefresher>().LoadFromDatabaseAsync(ct);

        DateTimeOffset nextCrypto = DateTimeOffset.MinValue, nextFx = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);

        do
        {
            try
            {
                var now = time.GetUtcNow();
                await using var scope = scopes.CreateAsyncScope();
                var refresher = scope.ServiceProvider.GetRequiredService<PriceRefresher>();

                if (now >= nextFx)
                {
                    var ok = await refresher.RefreshFxAsync(ct);
                    // Si falló, se reintenta en 10 min en vez de esperar horas.
                    nextFx = now + (ok ? TimeSpan.FromHours(options.Value.FxRefreshHours) : TimeSpan.FromMinutes(10));
                }
                if (now >= nextCrypto)
                {
                    await refresher.RefreshCryptoAsync(ct);
                    nextCrypto = now + TimeSpan.FromMinutes(options.Value.CryptoRefreshMinutes);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Error inesperado al refrescar precios");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
