using FinDash.Core.Models;

namespace FinDash.Core.Services;

/// <summary>
/// Reglas de las alertas (D11): detección de cruce + rearme + cooldown.
///
/// - Una alerta armada se dispara cuando el precio cumple la condición ("sube de" = precio ≥
///   umbral; "baja de" = precio ≤ umbral) y ya pasó el cooldown desde el último disparo.
/// - Después de dispararse queda en Triggered: aunque el precio se quede del mismo lado, no
///   vuelve a avisar en cada refresco.
/// - Se rearma cuando el precio vuelve al otro lado del umbral.
/// - Si el usuario la crea con la condición ya cumplida, avisa una vez en la siguiente evaluación.
/// </summary>
public static class AlertRuleEngine
{
    public const int DefaultCooldownMinutes = 60;

    public static bool IsMet(AlertCondition condition, decimal threshold, decimal price) =>
        condition == AlertCondition.Above ? price >= threshold : price <= threshold;

    public static AlertDecision Evaluate(
        AlertCondition condition,
        decimal threshold,
        bool isEnabled,
        AlertState state,
        DateTimeOffset? lastTriggeredAt,
        int cooldownMinutes,
        decimal currentPrice,
        DateTimeOffset now)
    {
        // Sin precio (activo sin cotización todavía) no se decide nada.
        if (!isEnabled || currentPrice <= 0) return AlertDecision.None;

        var met = IsMet(condition, threshold, currentPrice);

        if (!met)
            return state == AlertState.Triggered ? AlertDecision.Rearm : AlertDecision.None;

        if (state != AlertState.Armed) return AlertDecision.None;

        var cooldownOver = lastTriggeredAt is null
            || now - lastTriggeredAt.Value >= TimeSpan.FromMinutes(Math.Max(0, cooldownMinutes));
        return cooldownOver ? AlertDecision.Fire : AlertDecision.None;
    }
}
