using Dusiburg.AI.Ticket.Core.Context;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Core.Feedback;
using Dusiburg.AI.Ticket.Core.Pipeline;
using Dusiburg.AI.Ticket.Core.Publishing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Pipeline dell'agente: manopole, contesto, modello, pubblicazione, elaborazioni, esiti. Scoped come il contesto EF. Richiede
    /// <c>AddTicketAi</c>, <c>AddTicketRag</c>, <c>AddTicketZammad</c>, il <c>TicketDbContext</c> e un <see cref="TimeProvider"/>.
    /// </summary>
    public static IServiceCollection AddTicketCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => AgentOptions.FromConfiguration(configuration));
        services.AddSingleton<TicketLanguageModel>();
        services.AddScoped<TicketContextLoader>();
        services.AddScoped<ZammadPublisher>();
        services.AddScoped<TicketPipeline>();
        services.AddScoped<AgentRunService>();
        services.AddScoped<DraftOutcomeService>();
        services.AddScoped<Evaluation.DraftEvaluator>();

        return services;
    }
}
