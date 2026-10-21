using Dusiburg.AI.Ticket.Ai.Ollama;
using Dusiburg.AI.Ticket.Shared.Telemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Dusiburg.AI.Ticket.Ai;

/// <summary>
/// Embedding sempre in locale su Ollama (PLAN §4): il resto del codice vede solo
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>.
/// </summary>
public sealed class EmbeddingGeneratorFactory(AiOptions options, ILoggerFactory loggerFactory)
{
    public IEmbeddingGenerator<string, Embedding<float>> Create()
    {
        IEmbeddingGenerator<string, Embedding<float>> inner = new OllamaEmbeddingGenerator(
            OllamaClients.Create(options.OllamaEndpoint, OllamaClients.GenerationTimeout),
            options.EmbeddingModel,
            options.EmbeddingDimensions);

        return inner
            .AsBuilder()
            .UseLogging(loggerFactory)
            .UseOpenTelemetry(loggerFactory, TicketTelemetry.Sources.Embedding)
            .Build();
    }
}
