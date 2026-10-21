using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Dusiburg.AI.Ticket.Agentic;

/// <summary>I tool del server MCP per un'elaborazione; la connessione si chiude a fine elaborazione.</summary>
public interface IToolSession : IAsyncDisposable
{
    IReadOnlyList<AIFunction> Tools { get; }
}

public interface IToolCatalog
{
    Task<IToolSession> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Tool di <c>ticket-mcp</c> via HTTP, con la chiave nell'header <c>X-Api-Key</c> (come <c>McpToolCatalog</c> di Trasporto). Una
/// connessione per elaborazione: il server è stateless e la GPU è seriale, quindi non c'è nulla da guadagnare tenendola aperta.
/// </summary>
public sealed class McpToolCatalog(IHttpClientFactory httpClientFactory, ToolAgentOptions options, ILoggerFactory loggerFactory) : IToolCatalog
{
    public const string HttpClientName = "ticket-mcp";
    public const string ApiKeyHeaderName = "X-Api-Key";

    public async Task<IToolSession> OpenAsync(CancellationToken cancellationToken)
    {
        string key = options.McpKey
            ?? throw new InvalidOperationException($"{ToolAgentOptions.Keys.McpKey} non configurata: la modalità agente non raggiunge il server MCP (Parameters:ticket-mcp-key).");

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = options.McpUrl,
                Name = HttpClientName,
                AdditionalHeaders = new Dictionary<string, string> { [ApiKeyHeaderName] = key }
            },
            httpClientFactory.CreateClient(HttpClientName),
            loggerFactory,
            false);

        McpClient client = await McpClient.CreateAsync(transport, null, loggerFactory, cancellationToken);

        try
        {
            return new Session(client, [.. await client.ListToolsAsync(cancellationToken: cancellationToken)]);
        }
        catch
        {
            await client.DisposeAsync();

            throw;
        }
    }

    private sealed class Session(McpClient client, IReadOnlyList<AIFunction> tools) : IToolSession
    {
        public IReadOnlyList<AIFunction> Tools { get; } = tools;

        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}
