using System.Net;
using System.Text;
using System.Text.Json;
using Dusiburg.AI.Ticket.Mcp.Hosting;
using Dusiburg.AI.Ticket.Mcp.Tools;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Errors;
using Dusiburg.AI.Ticket.Shared.Knowledge;
using Dusiburg.AI.Ticket.Shared.Serialization;
using Dusiburg.AI.Ticket.Zammad;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Dusiburg.AI.Ticket.Mcp.Tests;

/// <summary>
/// <c>ticket-mcp</c> in-process (G12): chiave, elenco dei tool in sola lettura, passaggio alle API di conoscenza dell'Agent e alla
/// ricerca di Zammad (entrambe finte), errori strutturati.
/// </summary>
public class TicketMcpServerTests
{
    private const string Key = "chiave-di-prova";

    private readonly FakeUpstream _agent = new();
    private readonly FakeUpstream _zammad = new();
    private WebApplicationFactory<McpEntryPoint> _mcp = null!;

    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [OneTimeSetUp]
    public void StartServer()
    {
        _agent.On("/api/knowledge/search", HttpStatusCode.OK, new KnowledgeSearchResult("FF", DeploymentMode.OnPrem, "5.1.2",
            [new KnowledgeSource(1, ChunkSource.HistoricalTicket, "HT-2025-0001", 0.03, 0.18, false, "FF · on-premise · tutte le versioni", "Errore FF-E2041.", [new KnowledgeChunkRef(11, 0.18)])]));
        _agent.On("/api/knowledge/sources/KB-NOPE", HttpStatusCode.NotFound, new { title = "Risorsa non trovata", detail = "Fonte 'KB-NOPE' non presente nell'indice." });
        _zammad.On("/api/v1/tickets/search", HttpStatusCode.OK, new[]
        {
            new { id = 1, number = "71170", title = "Il ticket in corso", state = "new", rif_storico = (string?)null, created_at = "2026-10-21T09:00:00Z" },
            new { id = 2, number = "71100", title = "Errore TLS", state = "closed", rif_storico = (string?)"HT-2024-0200", created_at = "2026-09-01T09:00:00Z" }
        });

        _mcp = new WebApplicationFactory<McpEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(TicketMcpServer.ApiKeyConfigurationKey, Key);
            builder.UseSetting(KnowledgeApiClient.BaseAddressConfigurationKey, "http://agent.test");
            builder.UseSetting(ZammadOptions.Keys.BaseUrl, "http://zammad.test:8080");
            builder.UseSetting(ZammadOptions.Keys.ApiToken, "token");

            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient<KnowledgeApiClient>().ConfigurePrimaryHttpMessageHandler(() => _agent);
                services.AddHttpClient<ZammadClient>().ConfigurePrimaryHttpMessageHandler(() => _zammad);
            });
        });
    }

    [OneTimeTearDown]
    public async Task StopServer() => await _mcp.DisposeAsync();

    [TestCase(null)]
    [TestCase("chiave-sbagliata")]
    public async Task Mcp_WithoutTheKey_Is401(string? key)
    {
        //SETUP
        using HttpClient http = _mcp.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, TicketMcpServer.EndpointPath)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json")
        };

        if (key is not null)
        {
            request.Headers.Add(TicketMcpServer.ApiKeyHeaderName, key);
        }

        //SUT
        using HttpResponseMessage response = await http.SendAsync(request, CancellationToken);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await response.Content.ReadAsStringAsync(CancellationToken), Does.Contain(ErrorCodes.Unauthorized));
    }

    [Test]
    public async Task ToolsList_FiveReadOnlyTools()
    {
        //SETUP
        await using McpClient client = await ConnectAsync();

        //SUT
        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: CancellationToken);

        Assert.That(tools.Select(t => t.Name), Is.EquivalentTo(TicketToolNames.All));
        Assert.That(tools.Select(t => t.ProtocolTool.Annotations?.ReadOnlyHint), Has.All.True);
    }

    [Test]
    public async Task SearchSources_PassesOnlyTheGivenFiltersToTheAgent()
    {
        //SETUP
        await using McpClient client = await ConnectAsync();

        //SUT
        CallToolResult result = await client.CallToolAsync(TicketToolNames.SearchSources,
            new Dictionary<string, object?> { ["testo"] = "errore FF-E2041", ["prodotto"] = "FF", ["modalita"] = "OnPrem" }, cancellationToken: CancellationToken);

        Assert.That(result.IsError, Is.Not.True, Text(result));
        Assert.That(result.StructuredContent!.Value.Deserialize<KnowledgeSearchResult>(TicketJson.Options)!.Sources.Single().Key, Is.EqualTo("HT-2025-0001"));
        Assert.That(_agent.Requests.Last(r => r.AbsolutePath == "/api/knowledge/search").Query,
            Is.EqualTo("?text=errore%20FF-E2041&product=FF&mode=OnPrem&max=8"), "niente parametri vuoti");
    }

    [Test]
    public async Task ReadSource_UnknownKey_NotFoundWithTheAgentMessage()
    {
        //SETUP
        await using McpClient client = await ConnectAsync();

        //SUT
        CallToolResult result = await client.CallToolAsync(TicketToolNames.ReadSource,
            new Dictionary<string, object?> { ["chiave"] = "KB-NOPE" }, cancellationToken: CancellationToken);

        ToolError error = JsonSerializer.Deserialize<ToolErrorResponse>(Text(result), TicketJson.Options)!.Error;
        Assert.That(result.IsError, Is.True);
        Assert.That((error.Code, error.Message), Is.EqualTo((ErrorCodes.NotFound, "Fonte 'KB-NOPE' non presente nell'indice.")));
    }

    [Test]
    public async Task SearchZammad_ExcludesTheGivenTicketAndExposesTheHistoricalKey()
    {
        //SETUP
        await using McpClient client = await ConnectAsync();

        //SUT
        CallToolResult result = await client.CallToolAsync(TicketToolNames.SearchZammad,
            new Dictionary<string, object?> { ["testo"] = "TLS", ["escludiNumero"] = "71170" }, cancellationToken: CancellationToken);

        ZammadSearchResult found = result.StructuredContent!.Value.Deserialize<ZammadSearchResult>(TicketJson.Options)!;
        Assert.That(found.Tickets.Select(t => (t.Number, t.HistoricalKey)), Is.EqualTo(new[] { ("71100", (string?)"HT-2024-0200") }));
    }

    [Test]
    public async Task MissingRequiredArgument_IsAValidationError()
    {
        //SETUP
        await using McpClient client = await ConnectAsync();

        //SUT
        CallToolResult result = await client.CallToolAsync(TicketToolNames.CustomerHistory,
            new Dictionary<string, object?> { ["email"] = " " }, cancellationToken: CancellationToken);

        Assert.That(result.IsError, Is.True);
        Assert.That(Text(result), Does.Contain(ErrorCodes.ValidationError));
    }

    private async Task<McpClient> ConnectAsync()
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(_mcp.Server.BaseAddress, TicketMcpServer.EndpointPath),
                AdditionalHeaders = new Dictionary<string, string> { [TicketMcpServer.ApiKeyHeaderName] = Key }
            },
            _mcp.CreateClient(),
            null,
            true);

        return await McpClient.CreateAsync(transport, null, null, CancellationToken);
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    /// <summary>Servizio a valle finto: risponde per percorso con un JSON preparato e registra le richieste.</summary>
    private sealed class FakeUpstream : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = [];

        public List<Uri> Requests { get; } = [];

        public void On(string path, HttpStatusCode status, object body) =>
            _routes[path] = (status, JsonSerializer.Serialize(body, TicketJson.Options));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);

            (HttpStatusCode status, string body) = _routes.TryGetValue(request.RequestUri!.AbsolutePath, out var route)
                ? route
                : (HttpStatusCode.NotFound, """{"title":"percorso non previsto"}""");

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
