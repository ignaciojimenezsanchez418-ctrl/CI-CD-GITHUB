namespace FinDash.Api.Endpoints;

/// <summary>
/// Errores en formato ProblemDetails. El mensaje para mostrar al usuario va en "detail",
/// en español, igual que los OperationResult de la app.
/// </summary>
public static class Errors
{
    public static IResult BadRequest(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Solicitud no válida");

    public static IResult Unauthorized(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status401Unauthorized, title: "No autorizado");

    public static IResult NotFound(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status404NotFound, title: "No encontrado");

    public static IResult Conflict(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status409Conflict, title: "Conflicto");

    public static IResult Unavailable(string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Datos no disponibles");
}

public static class RateLimits
{
    public const string Auth = "auth";
}
