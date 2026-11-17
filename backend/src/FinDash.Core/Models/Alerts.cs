namespace FinDash.Core.Models;

public enum AlertCondition { Above, Below }

/// <summary>
/// Estado de una alerta encendida (D11). Armed: puede dispararse. Triggered: ya avisó y
/// espera a que el precio vuelva al otro lado del umbral para rearmarse.
/// </summary>
public enum AlertState { Armed, Triggered }

/// <summary>Qué debe hacer el servidor con una alerta después de ver el precio actual.</summary>
public enum AlertDecision { None, Fire, Rearm }
