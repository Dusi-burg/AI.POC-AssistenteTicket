using System.CommandLine;
using Dusiburg.AI.Ticket.Cli.Zammad;
using Dusiburg.AI.Ticket.Rag.Cleaning;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Demo;
using Dusiburg.AI.Ticket.Shared.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Cli.Commands;

/// <summary>
/// <c>search</c> per il debug del retrieval (fase-5.md §6): un testo con il contesto del cliente, oppure <c>--demo n</c> con testo e
/// cliente di uno scenario demo. Stampa le fonti con le posizioni nelle due classifiche, il punteggio e la penalità di versione.
/// </summary>
internal static class SearchCommand
{
    private const int TextPreview = 90;

    public static Command Create(IServiceProvider services)
    {
        var text = new Argument<string?>("testo") { Description = "Testo del ticket (tra virgolette).", Arity = ArgumentArity.ZeroOrOne };
        var product = new Option<string?>("--prodotto") { Description = "Codice del prodotto: FF o DA." };
        var mode = new Option<DeploymentMode?>("--modalita") { Description = "OnPrem o Saas." };
        var version = new Option<string?>("--versione") { Description = "Versione del cliente, es. 5.1.2." };
        var demo = new Option<int?>("--demo") { Description = $"Testo e cliente dello scenario demo (1-{DemoCatalog.Scenarios.Count}) al posto del testo." };
        var top = new Option<int>("--top") { Description = "Fonti da mostrare.", DefaultValueFactory = _ => 10 };
        var variant = new Option<RetrievalVariant>("--variante") { Description = "Hybrid, Vector o Bm25.", DefaultValueFactory = _ => RetrievalVariant.Hybrid };
        var noPenalty = new Option<bool>("--senza-penalita") { Description = "Non applica la penalità di versione." };

        var command = new Command("search", "Cerca le fonti per un testo o per uno scenario demo (debug del retrieval).")
        {
            text, product, mode, version, demo, top, variant, noPenalty
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            RetrievalQuery? query = parse.GetValue(demo) is { } scenario
                ? DemoQuery(scenario)
                : parse.GetValue(text) is { Length: > 0 } raw
                    ? new RetrievalQuery(SourceText.CustomerMessage(raw, KnownNames.None), parse.GetValue(product), parse.GetValue(mode), parse.GetValue(version))
                    : null;

            if (query is null)
            {
                Console.Error.WriteLine("Indicare il testo da cercare oppure --demo <n>.");
                return TicketCli.ExitError;
            }

            using IServiceScope scope = services.CreateScope();
            HybridRetriever retriever = scope.ServiceProvider.GetRequiredService<HybridRetriever>();
            var settings = new RetrievalSettings(parse.GetValue(variant), ApplyVersionPenalty: !parse.GetValue(noPenalty));
            RetrievalResult result = await retriever.SearchAsync(query, queryVector: null, settings, cancellationToken);

            Print(query, result, parse.GetValue(top));

            return 0;
        });

        return command;
    }

    /// <summary>Lo stesso testo che arriverà all'agente in Fase 6: oggetto e corpo di <c>data/demo.json</c>, contesto da <see cref="DemoCatalog"/>.</summary>
    private static RetrievalQuery DemoQuery(int scenarioNumber)
    {
        DemoScenario scenario = DemoCatalog.Scenarios.SingleOrDefault(s => s.Number == scenarioNumber)
            ?? throw new InvalidOperationException($"Scenario {scenarioNumber} inesistente: scegliere fra 1 e {DemoCatalog.Scenarios.Count}.");
        DemoTicketDefinition ticket = DemoTicketCreator.Load(SeedDataFiles.Directory()).Single(t => t.Scenario == scenarioNumber);
        DemoCustomer customer = DemoCatalog.Customer(scenario.CustomerEmailDomain);
        var names = KnownNames.Of($"{ticket.ContactFirstName} {ticket.ContactLastName}", customer.Name);

        Console.WriteLine($"Scenario {scenario.Number} — {scenario.Title}");
        Console.WriteLine($"Atteso: {scenario.ExpectedBehavior}");

        return new RetrievalQuery(SourceText.Query(ticket.Subject, ticket.Body, names), customer.ProductCode, customer.DeploymentMode, customer.Version);
    }

    private static void Print(RetrievalQuery query, RetrievalResult result, int top)
    {
        AppliedFilters filters = result.Filters;

        Console.WriteLine();
        Console.WriteLine($"Query: {query.Text.ReplaceLineEndings(" ")}");
        Console.WriteLine($"Filtri: prodotto {query.ProductCode ?? "—"}{(filters.ProductId is null && query.ProductCode is not null ? " (non riconosciuto)" : "")}, " +
            $"modalità {filters.Mode?.ToString() ?? "—"}, versione {query.Version ?? "—"}" +
            $"{(filters.VersionSequence is { } s ? $" (n. {s})" : query.Version is not null ? " (non riconosciuta: nessuna penalità)" : "")}");
        Console.WriteLine($"Tempi: embedding {result.Timings.Embedding.TotalMilliseconds:0} ms, SQL {result.Timings.Vector.TotalMilliseconds:0} ms, " +
            $"BM25 {result.Timings.Bm25.TotalMilliseconds:0.0} ms, totale {result.Timings.Total.TotalMilliseconds:0} ms");
        Console.WriteLine();
        Console.WriteLine($"Fonti del prompt (P): {string.Join(", ", result.PromptSources.Select(s => s.Key))}");
        Console.WriteLine();
        Console.WriteLine($"{"#",2}  {"Fonte",-16} {"Punteggio",9}  {"Vett.",-12} {"BM25",4}  {"Pen.",4}  {"",1}  Testo");

        foreach (RetrievedSource source in result.Sources.Take(top))
        {
            RetrievedChunk best = source.Best;
            string vector = best.VectorRank is { } rank ? $"{rank} ({best.VectorDistance:0.000})" : "—";
            string preview = best.Chunk.Text.ReplaceLineEndings(" ");

            Console.WriteLine($"{source.Rank,2}  {source.Key,-16} {source.Score,9:0.00000}  {vector,-12} {(best.Bm25Rank?.ToString() ?? "—"),4}  " +
                $"{(best.VersionPenalized ? "sì" : ""),4}  {(result.PromptSources.Contains(source) ? "P" : ""),1}  {(preview.Length > TextPreview ? preview[..TextPreview] + "…" : preview)}");
        }
    }
}
