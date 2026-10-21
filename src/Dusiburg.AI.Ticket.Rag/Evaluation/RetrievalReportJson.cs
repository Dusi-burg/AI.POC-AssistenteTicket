namespace Dusiburg.AI.Ticket.Rag.Evaluation;

/// <summary>Forma del JSON di <c>eval retrieval</c>, per il debug: metriche per variante e, per ogni caso, le prime fonti di ciascuna.</summary>
public sealed record RetrievalReportJson(
    DateTimeOffset RunAt,
    string EmbeddingModel,
    object Options,
    IReadOnlyList<RetrievalReportJson.VariantJson> Variants,
    IReadOnlyList<RetrievalReportJson.CaseJson> Cases)
{
    private const int SourcesKept = 10;

    public sealed record VariantJson(string Name, MetricSummary Overall, IReadOnlyDictionary<string, MetricSummary> ByGroup, double MeanLatencyMs, double P95LatencyMs);

    public sealed record CaseJson(
        string Ticket, string? CaseCode, string? Trap, string Product, string Mode, string? Version, IReadOnlyList<string> Relevant,
        IReadOnlyDictionary<string, CaseVariantJson> Variants);

    public sealed record CaseVariantJson(int? FirstRelevantRank, IReadOnlyList<string> Sources);

    public static RetrievalReportJson From(RetrievalEvaluation evaluation)
    {
        IEnumerable<EvalCaseResult> all = evaluation.Variants.SelectMany(v => v.Results).Concat(evaluation.Traps);
        Dictionary<string, Dictionary<string, CaseVariantJson>> byTicket = [];

        foreach (VariantEvaluation variant in evaluation.Variants)
        {
            foreach (EvalCaseResult result in variant.Results)
            {
                Add(byTicket, result, variant.Name);
            }
        }

        foreach (EvalCaseResult trap in evaluation.Traps)
        {
            Add(byTicket, trap, evaluation.Default.Name);
        }

        List<CaseJson> cases = [.. all
            .Select(r => r.Case)
            .DistinctBy(c => c.TicketNumber)
            .OrderBy(c => c.TicketNumber, StringComparer.Ordinal)
            .Select(c => new CaseJson(c.TicketNumber, c.CaseCode, c.Trap?.ToString(), c.ProductCode, c.Mode.ToString(), c.Version,
                [.. c.Relevant.Order(StringComparer.Ordinal)], byTicket[c.TicketNumber]))];

        return new RetrievalReportJson(
            evaluation.RunAt,
            evaluation.EmbeddingModel,
            evaluation.Options,
            [.. evaluation.Variants.Select(v => new VariantJson(v.Name, v.Overall, v.ByGroup, v.MeanLatencyMs, v.P95LatencyMs))],
            cases);
    }

    private static void Add(Dictionary<string, Dictionary<string, CaseVariantJson>> byTicket, EvalCaseResult result, string variant)
    {
        if (!byTicket.TryGetValue(result.Case.TicketNumber, out Dictionary<string, CaseVariantJson>? variants))
        {
            variants = [];
            byTicket[result.Case.TicketNumber] = variants;
        }

        variants[variant] = new CaseVariantJson(result.FirstRelevantRank, [.. result.Sources.Take(SourcesKept).Select(s => s.Key)]);
    }
}
