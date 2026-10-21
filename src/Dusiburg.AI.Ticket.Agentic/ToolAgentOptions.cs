using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Dusiburg.AI.Ticket.Agentic;

/// <summary>Manopole della modalità agente (Fase 9b): dove sta il server MCP, la sua chiave, quante chiamate ai tool per ticket.</summary>
public sealed record ToolAgentOptions(Uri McpUrl, string? McpKey, int MaxToolCalls)
{
    /// <summary>Risorsa <c>ticket-mcp</c> dell'AppHost, risolta dal service discovery.</summary>
    public const string DefaultMcpUrl = "http://ticket-mcp/mcp";

    /// <summary>Fuori dall'AppHost (Cli): la porta fissa dei launchSettings.</summary>
    public const string LocalMcpUrl = "http://localhost:5403/mcp";

    public const int DefaultMaxToolCalls = 6;

    public static class Keys
    {
        public const string McpUrl = "TICKET_MCP_URL";
        public const string McpKey = "TICKET_MCP_KEY";
        public const string MaxToolCalls = "AGENT_MAX_TOOL_CALLS";

        public static IReadOnlyList<string> All => [McpUrl, MaxToolCalls];
    }

    public static ToolAgentOptions FromConfiguration(IConfiguration configuration, string defaultMcpUrl = DefaultMcpUrl)
    {
        string url = configuration[Keys.McpUrl] is { Length: > 0 } configured ? configured : defaultMcpUrl;
        string? max = configuration[Keys.MaxToolCalls];

        return new ToolAgentOptions(
            Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https"
                ? uri
                : throw new InvalidOperationException($"Configurazione non valida: {Keys.McpUrl} '{url}' non è un indirizzo http(s) valido."),
            configuration[Keys.McpKey] is { Length: > 0 } key ? key : null,
            string.IsNullOrWhiteSpace(max)
                ? DefaultMaxToolCalls
                : int.TryParse(max.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int calls) && calls is >= 1 and <= 20
                    ? calls
                    : throw new InvalidOperationException($"Configurazione non valida: {Keys.MaxToolCalls} '{max}' non valido: serve un intero tra 1 e 20."));
    }
}
