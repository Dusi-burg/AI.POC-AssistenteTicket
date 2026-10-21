using Dusiburg.AI.Ticket.Core.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Agentic;

public static class AgenticServiceCollectionExtensions
{
    /// <summary>
    /// Modalità agente (Fase 9b): opzioni, client HTTP verso <c>ticket-mcp</c>, catalogo dei tool e <see cref="IToolAgentDrafter"/>
    /// per la pipeline. Si usa solo con <c>PIPELINE_MODE=agent</c>, ma registrarla non costa nulla: la connessione si apre a ogni
    /// elaborazione. Richiede <c>AddTicketAi</c>.
    /// </summary>
    public static IServiceCollection AddTicketAgentic(this IServiceCollection services, IConfiguration configuration, string defaultMcpUrl = ToolAgentOptions.DefaultMcpUrl)
    {
        services.AddSingleton(_ => ToolAgentOptions.FromConfiguration(configuration, defaultMcpUrl));
        services.AddHttpClient(McpToolCatalog.HttpClientName);
        services.AddSingleton<IToolCatalog, McpToolCatalog>();
        services.AddSingleton<IToolAgentDrafter, ToolAgentDrafter>();

        return services;
    }
}
