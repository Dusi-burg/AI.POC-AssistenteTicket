namespace Dusiburg.AI.Ticket.Shared.Errors;

/// <summary>Codici d'errore delle API: estensione <c>code</c> dei ProblemDetails.</summary>
public static class ErrorCodes
{
    public const string ValidationError = "VALIDATION_ERROR";
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string UpstreamUnavailable = "UPSTREAM_UNAVAILABLE";
    public const string Internal = "INTERNAL";
}

/// <summary>Errore di un tool MCP (Fase 9b): l'involucro <c>{ error: { code, message } }</c> che il modello legge al posto del risultato.</summary>
public sealed record ToolError(string Code, string Message);

public sealed record ToolErrorResponse(ToolError Error)
{
    public static ToolErrorResponse Create(string code, string message) => new(new ToolError(code, message));
}
