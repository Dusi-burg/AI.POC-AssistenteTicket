using Dusiburg.AI.Ticket.Mcp.Hosting;
using Dusiburg.AI.Ticket.Mcp.Tools;
using Dusiburg.AI.Ticket.Zammad;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddProblemDetails();

// Agent tramite service discovery (AppHost: WithReference(agent)); l'indirizzo si può sovrascrivere, ad esempio nei test.
builder.Services.AddHttpClient<KnowledgeApiClient>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()[KnowledgeApiClient.BaseAddressConfigurationKey] ?? KnowledgeApiClient.DefaultBaseAddress));

// Zammad con il token di ai-assistant, usato solo in lettura (ricerca dei ticket).
builder.Services.AddTicketZammad(builder.Configuration);

// Server MCP stateless su /mcp, raggiungibile solo con la chiave (TICKET_MCP_KEY, passata dall'AppHost).
builder.AddTicketMcpServer<TicketTools>();

var app = builder.Build();

app.UseExceptionHandler();

app.MapDefaultEndpoints();

app.MapGet("/", () => new { service = "Dusiburg.AI.Ticket.Mcp", phase = "9b", mcp = TicketMcpServer.EndpointPath });

app.MapTicketMcp();

app.Run();

/// <summary>Tipo di riferimento dell'assembly per <c>WebApplicationFactory</c> nei test.</summary>
public sealed class McpEntryPoint;
