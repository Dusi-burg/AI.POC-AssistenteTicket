using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Data.Seed;
using Dusiburg.AI.Ticket.Rag.Cleaning;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Rag.Evaluation;

public sealed record EvalProgress(int Done, int Total);

/// <summary>
/// <c>eval retrieval</c> (fase-5.md §6): i ticket del test set come query, con il contesto del loro cliente, contro l'indice.
/// La verità di riferimento è gratuita grazie al metodo ibrido della Fase 3: sono rilevanti i ticket indicizzati dello stesso caso
/// e gli articoli e le note che il caso cita. Due testi per caso (<see cref="EvalQueryKind"/>); l'embedding di ciascuno si calcola
/// una volta e serve a tutte le varianti.
/// </summary>
public sealed class RetrievalEvaluator(
    TicketDbContext db, HybridRetriever retriever, IEmbeddingService embeddings, AiOptions aiOptions, TimeProvider timeProvider)
{
    public const int MaxRank = 10;

    public async Task<RetrievalEvaluation> RunAsync(SeedData seed, IProgress<EvalProgress>? progress, CancellationToken cancellationToken)
    {
        (IReadOnlyList<EvalCase> cases, IReadOnlyDictionary<string, string?> caseOfTicket) = await LoadCasesAsync(seed, cancellationToken);
        var results = new Dictionary<(EvalQueryKind, EvalVariant), List<EvalCaseResult>>();
        List<EvalCaseResult> trapResults = [];
        int done = 0;

        foreach (EvalCase evalCase in cases)
        {
            bool measurable = evalCase.Trap is null && evalCase.Relevant.Count > 0;

            foreach (EvalQueryKind kind in measurable ? Enum.GetValues<EvalQueryKind>() : [EvalQueryKind.Full])
            {
                RetrievalQuery query = evalCase.QueryFor(kind);
                float[] vector = await embeddings.EmbedQueryAsync(query.Text, cancellationToken);

                foreach (EvalVariant variant in measurable ? EvalVariant.All : [EvalVariant.All.Single(v => v.Settings == RetrievalSettings.Default)])
                {
                    RetrievalResult result = await retriever.SearchAsync(query, vector, variant.Settings, cancellationToken);
                    EvalCaseResult caseResult = Score(evalCase, result);

                    if (measurable)
                    {
                        results.TryAdd((kind, variant), []);
                        results[(kind, variant)].Add(caseResult);
                    }
                    else if (evalCase.Trap is not null)
                    {
                        trapResults.Add(caseResult);
                    }
                }
            }

            progress?.Report(new EvalProgress(++done, cases.Count));
        }

        Dictionary<ChunkSource, int> indexed = await db.Chunks
            .GroupBy(c => c.Source)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);

        return new RetrievalEvaluation(
            timeProvider.GetLocalNow(),
            aiOptions.EmbeddingModel,
            retriever.Options,
            indexed,
            cases.Count(c => c.Trap is null && c.Relevant.Count == 0),
            [.. results.Select(r => Summarize(r.Key.Item1, r.Key.Item2, r.Value))],
            trapResults,
            caseOfTicket);
    }

    internal static EvalCaseResult Score(EvalCase evalCase, RetrievalResult result)
    {
        List<string> ranked = [.. result.Sources.Select(s => s.Key)];
        List<string> prompt = [.. result.PromptSources.Select(s => s.Key)];

        return new EvalCaseResult(
            evalCase,
            result.Sources,
            result.PromptSources,
            EvalMetrics.FirstRelevantRank(evalCase.Relevant, ranked, MaxRank),
            prompt.Count == 0 ? 0 : (double)prompt.Count(evalCase.Relevant.Contains) / prompt.Count,
            evalCase.RelevantDocuments.Count == 0 ? null : prompt.Any(evalCase.RelevantDocuments.Contains),
            result.Timings);
    }

    private static VariantEvaluation Summarize(EvalQueryKind kind, EvalVariant variant, List<EvalCaseResult> results)
    {
        Dictionary<string, MetricSummary> groups = new(StringComparer.Ordinal);

        foreach (IGrouping<string, EvalCaseResult> group in results.GroupBy(r => r.Case.ProductCode).Concat(results.GroupBy(r => r.Case.Mode.ToString())))
        {
            groups[group.Key] = MetricSummary.Of([.. group]);
        }

        List<double> latencies = [.. results.Select(r => r.Timings.Total.TotalMilliseconds)];

        return new VariantEvaluation(
            kind, variant, MetricSummary.Of(results), groups, EvalMetrics.Mean(latencies), EvalMetrics.Percentile(latencies, 95), results);
    }

    /// <summary>
    /// I ticket del test set con la query costruita come in produzione e le fonti rilevanti del loro caso. Li usa anche la
    /// valutazione delle bozze (Fase 8), con la stessa verità di riferimento.
    /// </summary>
    public async Task<(IReadOnlyList<EvalCase> Cases, IReadOnlyDictionary<string, string?> CaseOfTicket)> LoadCasesAsync(
        SeedData seed, CancellationToken cancellationToken)
    {
        Dictionary<short, string> products = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken);
        Dictionary<int, string> versions = await db.ProductVersions.AsNoTracking().ToDictionaryAsync(v => v.Id, v => v.Version, cancellationToken);
        Dictionary<int, string> customers = await db.Customers.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        // Ticket nell'indice per (caso, prodotto): sono le fonti "ticket" rilevanti.
        var indexed = await db.Chunks
            .Where(c => c.HistoricalTicketId != null)
            .Join(db.HistoricalTickets, c => c.HistoricalTicketId, t => t.Id, (c, t) => new { t.Number, t.CaseCode, t.ProductId })
            .ToListAsync(cancellationToken);
        ILookup<(string?, short), string> indexedByCase = indexed.ToLookup(t => (t.CaseCode, t.ProductId), t => t.Number);
        HashSet<string> indexedDocuments =
        [
            .. await db.Chunks.Where(c => c.KbArticleId != null).Join(db.KbArticles, c => c.KbArticleId, a => a.Id, (c, a) => a.Code).ToListAsync(cancellationToken),
            .. await db.Chunks.Where(c => c.ReleaseNoteId != null).Join(db.ReleaseNotes, c => c.ReleaseNoteId, n => n.Id, (c, n) => n.Code).ToListAsync(cancellationToken)
        ];

        List<HistoricalTicket> testSet = await db.HistoricalTickets.AsNoTracking()
            .Where(t => t.IsTestSet)
            .Include(t => t.Messages)
            .OrderBy(t => t.Number)
            .ToListAsync(cancellationToken);

        List<EvalCase> cases = [.. testSet.Select(ticket =>
        {
            string product = products[ticket.ProductId];
            string? version = ticket.ProductVersionId is { } id ? versions[id] : null;
            CaseInstance? instance = seed.Cases.SingleOrDefault(c => c.Code == ticket.CaseCode && c.Product == product);
            HashSet<string> documents = instance is null ? [] : [.. instance.Kb.Concat(instance.ReleaseNotes).Where(indexedDocuments.Contains)];
            HashSet<string> relevant = [.. indexedByCase[(ticket.CaseCode, ticket.ProductId)], .. documents];
            var names = KnownNames.Of(ticket.ContactName, customers[ticket.CustomerId]);

            string body = ticket.Messages.OrderBy(m => m.Sequence).FirstOrDefault()?.Body ?? ticket.Description;
            string query = SourceText.Query(ticket.Subject, body, names);

            // La variante di sintomo riservata al test set è l'ultima del caso (fase-3.md §0); i trabocchetti sono scritti per intero.
            string? symptom = ticket.TrapKind is null && instance is not null
                ? SourceText.CustomerMessage(instance.Expand(instance.Definition.Symptoms[^1], version), names)
                : null;

            return new EvalCase(ticket.Number, ticket.CaseCode, ticket.TrapKind, product, ticket.DeploymentMode, version, query, symptom,
                relevant, documents);
        })];

        return (cases, indexed.ToDictionary(t => t.Number, t => t.CaseCode));
    }
}
