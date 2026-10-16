using System.CommandLine;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dusiburg.AI.Ticket.Rag.Evaluation;
using Dusiburg.AI.Ticket.Shared.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Cli.Commands;

/// <summary><c>eval retrieval</c>: hit@k e MRR sul test set, report in <c>eval/</c> (fase-5.md §6). La valutazione completa è della Fase 8.</summary>
internal static class EvalCommand
{
    public static Command Create(IServiceProvider services) => new("eval", "Valutazione offline del POC.")
    {
        Retrieval(services)
    };

    private static Command Retrieval(IServiceProvider services)
    {
        var command = new Command("retrieval", "hit@1/3/5 e MRR@10 del retrieval sul test set, per variante; report Markdown e JSON in eval/.");

        command.SetAction(async (_, cancellationToken) =>
        {
            using IServiceScope scope = services.CreateScope();
            RetrievalEvaluator evaluator = scope.ServiceProvider.GetRequiredService<RetrievalEvaluator>();

            Console.WriteLine("Valutazione del retrieval sul test set…");
            RetrievalEvaluation evaluation = await evaluator.RunAsync(SeedDataFiles.Load(), new ConsoleProgress(), cancellationToken);

            string directory = Path.Combine(Path.GetDirectoryName(SeedDataFiles.Directory())!, "eval");
            Directory.CreateDirectory(directory);
            string name = $"retrieval-{evaluation.RunAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
            string markdown = Path.Combine(directory, $"{name}.md");
            string json = Path.Combine(directory, $"{name}.json");

            await File.WriteAllTextAsync(markdown, RetrievalReportRenderer.Render(evaluation), cancellationToken);
            await File.WriteAllTextAsync(json, JsonSerializer.Serialize(RetrievalReportJson.From(evaluation), JsonOptions), cancellationToken);

            Console.WriteLine();

            foreach (VariantEvaluation variant in evaluation.Variants)
            {
                Console.WriteLine($"  {variant.Name,-52} hit@5 {variant.Overall.HitAt5,6:P1}  MRR@10 {variant.Overall.Mrr10:0.000}  prompt {variant.Overall.PromptPrecision,6:P1}");
            }

            Console.WriteLine();
            Console.WriteLine($"Report: {markdown}");

            return 0;
        });

        return command;
    }

    /// <summary>Come le API (camelCase, enum come stringhe), indentato e con gli accenti leggibili.</summary>
    private static readonly JsonSerializerOptions JsonOptions =
        new(TicketJson.Options) { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed class ConsoleProgress : IProgress<EvalProgress>
    {
        private int _lastDecile = -1;

        public void Report(EvalProgress value)
        {
            int decile = value.Total == 0 ? 10 : value.Done * 10 / value.Total;

            if (decile != _lastDecile)
            {
                _lastDecile = decile;
                Console.WriteLine($"  {value.Done}/{value.Total} ticket");
            }
        }
    }
}
