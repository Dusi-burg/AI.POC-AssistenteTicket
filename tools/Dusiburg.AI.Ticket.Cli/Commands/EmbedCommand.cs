using System.CommandLine;
using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Rag.Indexing;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Cli.Commands;

/// <summary><c>embed [--solo-mancanti]</c>: chunk e vettori dell'indice (fase-5.md §4).</summary>
internal static class EmbedCommand
{
    public static Command Create(IServiceProvider services)
    {
        var onlyMissing = new Option<bool>("--solo-mancanti")
        {
            Description = "Aggiunge solo i chunk delle fonti nuove e i vettori mancanti, senza ricostruire l'indice."
        };
        var command = new Command("embed", "Costruisce i chunk da ticket risolti, articoli KB e note di rilascio e ne calcola gli embedding.") { onlyMissing };

        command.SetAction(async (parse, cancellationToken) =>
        {
            using IServiceScope scope = services.CreateScope();
            IndexingPipeline pipeline = scope.ServiceProvider.GetRequiredService<IndexingPipeline>();
            AiOptions options = services.GetRequiredService<AiOptions>();
            bool missing = parse.GetValue(onlyMissing);

            Console.WriteLine($"Embedding con {options.EmbeddingModel} ({options.EmbeddingDimensions}) su {options.OllamaEndpoint}" +
                (missing ? ", solo i mancanti" : ", indice ricostruito da zero"));

            EmbedResult result = await pipeline.RunAsync(new EmbedRequest(missing), new ConsoleProgress(), cancellationToken);

            Console.WriteLine();
            Console.WriteLine($"Fatto in {result.Duration.TotalSeconds:0.0} s: {result.ChunksRemoved} chunk rimossi, {result.ChunksCreated} creati, " +
                $"{result.Vectors} vettori calcolati.");
            Console.WriteLine($"Indice: {string.Join(", ", result.ChunksBySource.OrderBy(s => s.Key).Select(s => $"{s.Key} {s.Value}"))}; " +
                $"senza vettore {result.WithoutEmbedding}.");

            return result.WithoutEmbedding == 0 ? 0 : TicketCli.ExitError;
        });

        return command;
    }

    /// <summary>Una riga ogni 10% circa.</summary>
    private sealed class ConsoleProgress : IProgress<EmbedProgress>
    {
        private int _lastDecile = -1;

        public void Report(EmbedProgress value)
        {
            int decile = value.Total == 0 ? 10 : value.Done * 10 / value.Total;

            if (decile != _lastDecile)
            {
                _lastDecile = decile;
                Console.WriteLine($"  {value.Done}/{value.Total} vettori");
            }
        }
    }
}
