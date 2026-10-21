using Dusiburg.AI.Ticket.Rag.Evaluation;
using Dusiburg.AI.Ticket.Rag.Indexing;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Rag;

public static class RagServiceCollectionExtensions
{
    /// <summary>
    /// Manopole del retrieval, catalogo dei chunk (singleton, ricaricato quando cambia l'indice), retriever, indicizzazione e
    /// valutazione (scoped, come il contesto EF). Richiede <c>AddTicketAi</c> e il <c>TicketDbContext</c>.
    /// </summary>
    public static IServiceCollection AddTicketRag(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => RetrievalOptions.FromConfiguration(configuration));
        services.AddSingleton<ChunkCatalogCache>();
        services.AddScoped<HybridRetriever>();
        services.AddScoped<IndexingPipeline>();
        services.AddScoped<RetrievalEvaluator>();

        return services;
    }
}
