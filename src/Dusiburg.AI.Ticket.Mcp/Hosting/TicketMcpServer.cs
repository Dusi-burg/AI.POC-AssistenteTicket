using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dusiburg.AI.Ticket.Shared.Errors;
using Dusiburg.AI.Ticket.Shared.Serialization;
using Dusiburg.AI.Ticket.Shared.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Dusiburg.AI.Ticket.Mcp.Hosting;

/// <summary>
/// Errore di un tool con un codice di <see cref="ErrorCodes"/>: il filtro lo restituisce come <c>{ error: { code, message } }</c>.
/// Il messaggio arriva al modello: niente dettagli interni.
/// </summary>
public sealed class ToolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Composizione del server MCP come i server di Trasporto: trasporto HTTP stateless su <c>/mcp</c>, raggiungibile solo con la
/// chiave (<c>X-Api-Key</c>), span ed errori strutturati su ogni chiamata. Qui un solo chiamante (la modalità agente dell'Agent e
/// la valutazione della Cli), quindi una chiave sola e nessuna matrice dei ruoli.
/// </summary>
public static class TicketMcpServer
{
    public const string EndpointPath = "/mcp";
    public const string ApiKeyHeaderName = "X-Api-Key";

    /// <summary>Chiave condivisa (<c>Parameters:ticket-mcp-key</c> negli user-secrets dell'AppHost).</summary>
    public const string ApiKeyConfigurationKey = "TICKET_MCP_KEY";

    private const string InternalErrorMessage = "Errore interno durante l'esecuzione del tool.";

    private static readonly ActivitySource Telemetry = new(TicketTelemetry.Sources.Mcp);

    // Testo destinato al modello: le lettere accentate restano leggibili.
    private static readonly JsonSerializerOptions ErrorJson = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Opzioni JSON dei tool: le stesse delle API (camelCase, enum come stringhe), con le lettere accentate leggibili.</summary>
    public static JsonSerializerOptions ToolSerializerOptions { get; } = CreateToolSerializerOptions();

    public static IMcpServerBuilder AddTicketMcpServer<TTools>(this IHostApplicationBuilder builder)
        where TTools : class =>
        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithRequestFilters(filters => filters.AddCallToolFilter(CallFilter))
            .WithTools<TTools>(ToolSerializerOptions);

    /// <summary>Endpoint MCP dietro la chiave: senza la chiave giusta la richiesta si ferma qui, con lo stesso involucro d'errore.</summary>
    public static IEndpointConventionBuilder MapTicketMcp(this WebApplication app)
    {
        string key = app.Configuration[ApiKeyConfigurationKey] is { Length: > 0 } configured
            ? configured
            : throw new InvalidOperationException($"{ApiKeyConfigurationKey} non configurata: il server MCP non parte senza chiave (Parameters:ticket-mcp-key).");
        byte[] expected = Encoding.UTF8.GetBytes(key);

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(EndpointPath),
            branch => branch.Use(async (http, next) =>
            {
                byte[] received = Encoding.UTF8.GetBytes(http.Request.Headers[ApiKeyHeaderName].ToString());

                if (!CryptographicOperations.FixedTimeEquals(received, expected))
                {
                    http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await http.Response.WriteAsJsonAsync(ToolErrorResponse.Create(ErrorCodes.Unauthorized, $"Intestazione {ApiKeyHeaderName} assente o non riconosciuta."));

                    return;
                }

                await next(http);
            }));

        return app.MapMcp(EndpointPath);
    }

    public static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(ToolErrorResponse.Create(code, message), ErrorJson) }]
    };

    /// <summary>
    /// Span <c>mcp.tool {nome}</c> con l'esito e traduzione delle eccezioni negli errori strutturati: nessuna eccezione arriva al
    /// client, che la mostrerebbe al modello come testo generico.
    /// </summary>
    private static McpRequestHandler<CallToolRequestParams, CallToolResult> CallFilter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            string toolName = context.Params?.Name ?? "unknown";
            ILogger logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger("Dusiburg.AI.Ticket.Mcp.ToolCalls") ?? NullLogger.Instance;

            using Activity? activity = Telemetry.StartActivity($"mcp.tool {toolName}");
            activity?.SetTag("tool.name", toolName);

            CallToolResult result;
            string? errorCode = null;

            try
            {
                result = await next(context, cancellationToken);
            }
            catch (ToolException exception)
            {
                errorCode = exception.Code;
                result = Error(exception.Code, exception.Message);
            }
            catch (McpProtocolException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException)
            {
                errorCode = ErrorCodes.ValidationError;
                result = Error(errorCode, exception.Message);
            }
            catch (HttpRequestException exception)
            {
                errorCode = ErrorCodes.UpstreamUnavailable;
                result = Error(errorCode, "Servizio a valle non raggiungibile.");
                logger.LogWarning(exception, "Tool {ToolName}: servizio a valle non raggiungibile", toolName);
            }
            catch (Exception exception)
            {
                errorCode = ErrorCodes.Internal;
                result = Error(errorCode, InternalErrorMessage);
                logger.LogError(exception, "Eccezione non gestita nel tool {ToolName}", toolName);
            }

            activity?.SetTag("tool.outcome", errorCode is null ? "ok" : $"error:{errorCode}");

            if (errorCode is not null)
            {
                activity?.SetStatus(ActivityStatusCode.Error, errorCode);
            }

            return result;
        };

    private static JsonSerializerOptions CreateToolSerializerOptions()
    {
        JsonSerializerOptions options = TicketJson.Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
        });

        options.MakeReadOnly();

        return options;
    }
}
