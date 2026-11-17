using FinDash.Core.Models;
using FinDash.Core.Services;

namespace FinDash.Tests;

/// <summary>US-402: cruce, no repetir, cooldown, desactivada y rearme.</summary>
public class AlertRuleEngineTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    static AlertDecision Eval(AlertCondition c, decimal threshold, decimal price,
        AlertState state = AlertState.Armed, bool enabled = true, DateTimeOffset? last = null, int cooldown = 60) =>
        AlertRuleEngine.Evaluate(c, threshold, enabled, state, last, cooldown, price, Now);

    [Fact]
    public void CruceHaciaArriba_Dispara()
    {
        Assert.Equal(AlertDecision.None, Eval(AlertCondition.Above, 100_000, 99_999));
        Assert.Equal(AlertDecision.Fire, Eval(AlertCondition.Above, 100_000, 100_000));
    }

    [Fact]
    public void CruceHaciaAbajo_Dispara()
    {
        Assert.Equal(AlertDecision.None, Eval(AlertCondition.Below, 500, 500.01m));
        Assert.Equal(AlertDecision.Fire, Eval(AlertCondition.Below, 500, 499.80m));
    }

    [Fact]
    public void PrecioQueSeQuedaArriba_NoRepite()
    {
        // Ya avisó y el precio sigue arriba: no se notifica en cada refresco.
        Assert.Equal(AlertDecision.None,
            Eval(AlertCondition.Above, 100, 120, AlertState.Triggered, last: Now.AddHours(-5)));
    }

    [Fact]
    public void Rearme_CuandoElPrecioVuelveAlOtroLado()
    {
        Assert.Equal(AlertDecision.Rearm, Eval(AlertCondition.Above, 100, 90, AlertState.Triggered, last: Now.AddMinutes(-1)));
        Assert.Equal(AlertDecision.None, Eval(AlertCondition.Above, 100, 90, AlertState.Armed));
    }

    [Fact]
    public void Cooldown_NoDisparaAntesDeTiempo()
    {
        // Se rearmó y volvió a cruzar a los 30 min: con cooldown de 60 todavía no avisa.
        Assert.Equal(AlertDecision.None, Eval(AlertCondition.Below, 500, 499, last: Now.AddMinutes(-30), cooldown: 60));
        Assert.Equal(AlertDecision.Fire, Eval(AlertCondition.Below, 500, 499, last: Now.AddMinutes(-60), cooldown: 60));
    }

    [Fact]
    public void AlertaDesactivada_NoHaceNada()
    {
        Assert.Equal(AlertDecision.None, Eval(AlertCondition.Above, 100, 200, enabled: false));
        Assert.Equal(AlertDecision.None, Eval(AlertCondition.Above, 100, 50, AlertState.Triggered, enabled: false));
    }

    [Fact]
    public void SinPrecio_NoDecideNada()
    {
        Assert.Equal(AlertDecision.None, Eval(AlertCondition.Below, 500, 0));
    }

    [Fact]
    public void SecuenciaCompleta_UnSoloAvisoPorCruce()
    {
        // Simula refrescos: 510, 499 (avisa), 498, 497 (no repite), 505 (rearma), 499 tras 2 h (avisa).
        var state = AlertState.Armed;
        DateTimeOffset? last = null;
        var t = Now;
        var fires = 0;

        foreach (var (price, minutes) in new (decimal, int)[] { (510, 0), (499, 1), (498, 2), (497, 3), (505, 4), (499, 120) })
        {
            var at = t.AddMinutes(minutes);
            switch (AlertRuleEngine.Evaluate(AlertCondition.Below, 500, true, state, last, 60, price, at))
            {
                case AlertDecision.Fire: state = AlertState.Triggered; last = at; fires++; break;
                case AlertDecision.Rearm: state = AlertState.Armed; break;
            }
        }

        Assert.Equal(2, fires);
    }
}
