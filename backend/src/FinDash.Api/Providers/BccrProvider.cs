using System.Globalization;
using System.Xml.Linq;
using FinDash.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace FinDash.Api.Providers;

public sealed class BccrOptions
{
    public string BaseUrl { get; set; } = "https://gee.bccr.fi.cr/Indicadores/Suscripciones/WS/wsindicadoreseconomicos.asmx/";
    public string Name { get; set; } = "FinDash";
    public string? Email { get; set; }
    public string? Token { get; set; }
}

/// <summary>
/// Tipo de cambio oficial desde el servicio web de Indicadores Económicos del BCCR:
/// indicador 317 (compra) y 318 (venta). Pide correo y token de suscripción (D13).
///
/// PENDIENTE (US-206): validar contra el servicio real en cuanto el equipo tenga el token.
/// El formato que se procesa está en ParseIndicator y tiene pruebas con una respuesta guardada.
/// </summary>
public sealed class BccrProvider(HttpClient http, IOptions<BccrOptions> options) : IExchangeRateProvider
{
    const int BuyIndicator = 317;
    const int SellIndicator = 318;

    public async Task<IReadOnlyList<DailyRate>> GetRatesAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var buy = await GetIndicatorAsync(BuyIndicator, from, to, ct);
        var sell = await GetIndicatorAsync(SellIndicator, from, to, ct);

        return buy
            .Where(b => sell.ContainsKey(b.Key))
            .Select(b => new DailyRate(b.Key, b.Value, sell[b.Key]))
            .OrderBy(r => r.Date)
            .ToList();
    }

    async Task<Dictionary<DateOnly, decimal>> GetIndicatorAsync(int indicator, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var o = options.Value;
        var url = "ObtenerIndicadoresEconomicosXML"
            + $"?Indicador={indicator}"
            + $"&FechaInicio={from:dd'/'MM'/'yyyy}"
            + $"&FechaFinal={to:dd'/'MM'/'yyyy}"
            + $"&Nombre={Uri.EscapeDataString(o.Name)}"
            + "&SubNiveles=N"
            + $"&CorreoElectronico={Uri.EscapeDataString(o.Email ?? "")}"
            + $"&Token={Uri.EscapeDataString(o.Token ?? "")}";

        var xml = await http.GetStringAsync(url, ct);
        return ParseIndicator(xml);
    }

    /// <summary>
    /// El servicio devuelve &lt;string&gt; con el XML de los datos escapado adentro:
    /// Datos_de_INGC011_CAT_INDICADORECONOMIC / INGC011_CAT_INDICADORECONOMIC con DES_FECHA y NUM_VALOR.
    /// También acepta el XML de datos sin envolver.
    /// </summary>
    internal static Dictionary<DateOnly, decimal> ParseIndicator(string xml)
    {
        var doc = XDocument.Parse(xml);
        if (doc.Root is { } root && root.Name.LocalName == "string" && !root.HasElements)
            doc = XDocument.Parse(root.Value);

        var result = new Dictionary<DateOnly, decimal>();
        foreach (var row in doc.Descendants().Where(e => e.Name.LocalName == "INGC011_CAT_INDICADORECONOMIC"))
        {
            var date = row.Elements().FirstOrDefault(e => e.Name.LocalName == "DES_FECHA")?.Value;
            var value = row.Elements().FirstOrDefault(e => e.Name.LocalName == "NUM_VALOR")?.Value;
            if (date is null || value is null) continue;
            if (!DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) || v <= 0) continue;
            result[DateOnly.FromDateTime(d.DateTime)] = v;
        }
        return result;
    }
}
