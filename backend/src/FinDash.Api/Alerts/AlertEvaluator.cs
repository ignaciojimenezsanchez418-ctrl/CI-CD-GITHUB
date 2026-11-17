using FinDash.Api.Data;
using FinDash.Api.Prices;
using FinDash.Core.Models;
using FinDash.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FinDash.Api.Alerts;

public sealed class AlertOptions
{
    public int EvaluateEverySeconds { get; set; } = 60;
}

/// <summary>
/// Compara las alertas encendidas de todos los usuarios con la caché de precios y deja un
/// AlertEvent por cada disparo (RF-11). La app los recoge al abrirse o al refrescar.
/// </summary>
public sealed class AlertEvaluator(FinDashDbContext db, QuoteStore store, TimeProvider time, ILogger<AlertEvaluator> log)
{
    /// <summary>Devuelve cuántas alertas se dispararon.</summary>
    public async Task<int> EvaluateAsync(CancellationToken ct)
    {
        var market = store.Current;
        if (market.Fx is null) return 0;

        var now = time.GetUtcNow();
        var alerts = await db.PriceAlerts.Where(a => a.IsEnabled).ToListAsync(ct);
        var fired = 0;

        foreach (var alert in alerts)
        {
            var quote = market.Quote(alert.AssetId);
            if (quote is null) continue;
            var price = alert.Currency == Currency.USD ? quote.PriceUsd : quote.PriceCrc;

            var decision = AlertRuleEngine.Evaluate(
                alert.Condition, alert.Threshold, alert.IsEnabled, alert.State,
                alert.LastTriggeredAt, alert.CooldownMinutes, price, now);

            switch (decision)
            {
                case AlertDecision.Fire:
                    alert.State = AlertState.Triggered;
                    alert.LastTriggeredAt = now;
                    db.AlertEvents.Add(new AlertEvent
                    {
                        AlertId = alert.Id,
                        UserId = alert.UserId,
                        AssetId = alert.AssetId,
                        Condition = alert.Condition,
                        Threshold = alert.Threshold,
                        Currency = alert.Currency,
                        PriceAtTrigger = price,
                        TriggeredAt = now,
                    });
                    fired++;
                    break;
                case AlertDecision.Rearm:
                    alert.State = AlertState.Armed;
                    break;
            }
        }

        await db.SaveChangesAsync(ct);
        if (fired > 0) log.LogInformation("Se dispararon {Count} alertas", fired);
        return fired;
    }
}

/// <summary>Servicio en segundo plano que evalúa las alertas cada minuto (US-403).</summary>
public sealed class AlertWorker(
    IServiceScopeFactory scopes,
    IOptions<AlertOptions> options,
    IOptions<WorkerOptions> workers,
    TimeProvider time,
    ILogger<AlertWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!workers.Value.Enabled) return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.EvaluateEverySeconds)), time);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AlertEvaluator>().EvaluateAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Error al evaluar alertas");
            }
        }
    }
}
