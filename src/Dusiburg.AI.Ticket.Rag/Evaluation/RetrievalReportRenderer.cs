using System.Globalization;
using System.Text;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Rag.Evaluation;

/// <summary>Report Markdown di <c>eval retrieval</c> in <c>eval/</c>: metriche per variante, gruppi, trabocchetti, casi mancati.</summary>
public static class RetrievalReportRenderer
{
    /// <summary>Soglia del PLAN §1: fonte corretta fra le prime 5 nell'80% dei casi.</summary>
    public const double HitAt5Target = 0.80;

    private const int MissesShown = 30;

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public static string Render(RetrievalEvaluation evaluation)
    {
        var md = new StringBuilder();
        VariantEvaluation main = evaluation.Default;
        VariantEvaluation symptom = evaluation.Of(EvalQueryKind.SymptomOnly);
        int prompt = evaluation.Options.PromptSourcesMax;

        md.AppendLine($"# Valutazione del retrieval — {evaluation.RunAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        md.AppendLine();
        md.AppendLine("> Generato da `dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- eval retrieval` (fase 5). Query **completa**: oggetto e");
        md.AppendLine("> primo messaggio dei ticket del test set, puliti e anonimizzati come i documenti. Query **solo sintomo**: la variante di sintomo");
        md.AppendLine("> riservata al test set, mai indicizzata (senza oggetto e dettagli, che i ticket simulati dello stesso caso condividono).");
        md.AppendLine("> Contesto (prodotto, modalità, versione) dal ticket. Rilevanti: i ticket indicizzati dello stesso caso (`CaseCode`) e gli");
        md.AppendLine($"> articoli e le note citati dal caso. Metriche per fonte; \"prompt\" = le prime {prompt} fonti (`PROMPT_SOURCES_MAX`).");
        md.AppendLine();
        md.AppendLine($"- Embedding: `{evaluation.EmbeddingModel}`; `RETRIEVAL_TOP_K` {evaluation.Options.TopK}, `RRF_K` {evaluation.Options.RrfK}, " +
            $"`RETRIEVAL_VERSION_PENALTY` {Number(evaluation.Options.VersionPenalty)}");
        md.AppendLine($"- Indice: {string.Join(", ", evaluation.IndexedChunks.OrderBy(c => c.Key).Select(c => $"{c.Value} chunk {SourceLabel(c.Key)}"))}");
        md.AppendLine($"- Casi misurati: {main.Overall.Cases} (esclusi {evaluation.SkippedWithoutRelevant} senza fonti rilevanti nell'indice e {evaluation.Traps.Count} trabocchetti, a parte)");
        md.AppendLine($"- Configurazione di default, query completa: **hit@5 {Percent(main.Overall.HitAt5)}** (soglia {Percent(HitAt5Target)}: " +
            $"{(main.Overall.HitAt5 >= HitAt5Target ? "raggiunta" : "**non raggiunta**")}), **MRR@10 {Number(main.Overall.Mrr10)}**, " +
            $"precisione del prompt {Percent(main.Overall.PromptPrecision)}");
        md.AppendLine($"- Solo sintomo: hit@5 {Percent(symptom.Overall.HitAt5)}, MRR@10 {Number(symptom.Overall.Mrr10)}, precisione del prompt {Percent(symptom.Overall.PromptPrecision)}");
        md.AppendLine();

        foreach (EvalQueryKind kind in Enum.GetValues<EvalQueryKind>())
        {
            AppendVariants(md, evaluation, kind);
        }

        AppendGroups(md, evaluation);
        AppendTraps(md, evaluation);

        foreach (EvalQueryKind kind in Enum.GetValues<EvalQueryKind>())
        {
            AppendMisses(md, evaluation, evaluation.Of(kind));
        }

        return md.ToString();
    }

    private static void AppendVariants(StringBuilder md, RetrievalEvaluation evaluation, EvalQueryKind kind)
    {
        md.AppendLine($"## Varianti — {KindLabel(kind)}");
        md.AppendLine();
        md.AppendLine("| Variante | hit@1 | hit@3 | hit@5 | MRR@10 | Precisione prompt | KB/nota nel prompt | Latenza media | p95 |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");

        foreach (VariantEvaluation variant in evaluation.Variants.Where(v => v.QueryKind == kind))
        {
            MetricSummary m = variant.Overall;
            md.AppendLine($"| {variant.Variant.Name} | {Percent(m.HitAt1)} | {Percent(m.HitAt3)} | {Percent(m.HitAt5)} | {Number(m.Mrr10)} | " +
                $"{Percent(m.PromptPrecision)} | {(m.PromptDocumentHit is { } d ? Percent(d) : "—")} | " +
                $"{variant.MeanLatencyMs.ToString("0", Italian)} ms | {variant.P95LatencyMs.ToString("0", Italian)} ms |");
        }

        md.AppendLine();

        if (kind == EvalQueryKind.Full)
        {
            md.AppendLine("Latenza per query senza l'embedding (calcolato una volta e condiviso fra le varianti): ricerca SQL, BM25 e fusione. " +
                "\"KB/nota nel prompt\": casi che citano un articolo o una nota, con almeno uno di essi fra le fonti del prompt.");
            md.AppendLine();
        }
    }

    private static void AppendGroups(StringBuilder md, RetrievalEvaluation evaluation)
    {
        string[] groups = [.. evaluation.Default.ByGroup.Keys.Order(StringComparer.Ordinal)];

        md.AppendLine("## Precisione del prompt per prodotto e modalità");
        md.AppendLine();
        md.AppendLine($"| Variante | {string.Join(" | ", groups.Select(g => $"{g} ({evaluation.Default.ByGroup[g].Cases})"))} |");
        md.AppendLine($"|---|{string.Concat(groups.Select(_ => "---|"))}");

        foreach (VariantEvaluation variant in evaluation.Variants)
        {
            md.AppendLine($"| {variant.Name} | {string.Join(" | ", groups.Select(g => variant.ByGroup.TryGetValue(g, out MetricSummary? m) ? Percent(m.PromptPrecision) : "—"))} |");
        }

        md.AppendLine();
    }

    private static void AppendTraps(StringBuilder md, RetrievalEvaluation evaluation)
    {
        md.AppendLine("## Trabocchetti (configurazione di default, query completa)");
        md.AppendLine();
        md.AppendLine("Con un caso atteso conta la posizione della prima fonte rilevante; senza (`NoHistory`, `Vague`) conta che le fonti trovate");
        md.AppendLine("siano deboli: il punteggio RRF massimo, con entrambe le classifiche al primo posto, è 2/(k+1). La decisione su chiarimenti ed");
        md.AppendLine("escalation è della Fase 6.");
        md.AppendLine();
        md.AppendLine("| Ticket | Tipo | Caso atteso | Prima rilevante | Prima fonte | Punteggio | Distanza coseno |");
        md.AppendLine("|---|---|---|---|---|---|---|");

        foreach (EvalCaseResult trap in evaluation.Traps)
        {
            RetrievedSource? first = trap.Sources.FirstOrDefault();
            md.AppendLine($"| {trap.Case.TicketNumber} | {trap.Case.Trap} | {trap.Case.CaseCode ?? "—"} | {Rank(trap)} | {first?.Key ?? "—"} | " +
                $"{(first is null ? "—" : first.Score.ToString("0.0000", Italian))} | {(first?.Best.VectorDistance is { } d ? Number(d) : "—")} |");
        }

        md.AppendLine();
    }

    private static void AppendMisses(StringBuilder md, RetrievalEvaluation evaluation, VariantEvaluation variant)
    {
        int prompt = evaluation.Options.PromptSourcesMax;
        List<EvalCaseResult> misses = [.. variant.Results.Where(r => !r.HitAt(5) || r.PromptPrecision < 0.5)];

        md.AppendLine($"## Casi deboli — {KindLabel(variant.QueryKind)}: {misses.Count}");
        md.AppendLine();
        md.AppendLine($"Configurazione di default: nessuna fonte rilevante fra le prime 5, oppure meno di metà delle {prompt} fonti del prompt rilevanti.");
        md.AppendLine();

        if (misses.Count == 0)
        {
            return;
        }

        md.AppendLine($"| Ticket | Caso | Modalità | Versione | Prima rilevante | Precisione prompt | Fonti del prompt (caso) |");
        md.AppendLine("|---|---|---|---|---|---|---|");

        foreach (EvalCaseResult miss in misses.Take(MissesShown))
        {
            string top = string.Join(", ", miss.PromptSources.Select(s =>
                s.Source == ChunkSource.HistoricalTicket && evaluation.CaseOfTicket.TryGetValue(s.Key, out string? caseCode) ? $"{s.Key} ({caseCode})" : s.Key));
            md.AppendLine($"| {miss.Case.TicketNumber} | {miss.Case.CaseCode} | {miss.Case.Mode} | {miss.Case.Version ?? "—"} | {Rank(miss)} | " +
                $"{Percent(miss.PromptPrecision)} | {top} |");
        }

        if (misses.Count > MissesShown)
        {
            md.AppendLine();
            md.AppendLine($"… altri {misses.Count - MissesShown} nel file JSON.");
        }

        md.AppendLine();
    }

    private static string KindLabel(EvalQueryKind kind) => kind == EvalQueryKind.Full ? "query completa" : "solo sintomo";

    private static string Rank(EvalCaseResult result) => result.FirstRelevantRank?.ToString(CultureInfo.InvariantCulture) ?? "oltre 10";

    private static string SourceLabel(ChunkSource source) => source switch
    {
        ChunkSource.HistoricalTicket => "ticket",
        ChunkSource.KbArticle => "KB",
        ChunkSource.ReleaseNote => "note di rilascio",
        _ => source.ToString()
    };

    private static string Percent(double value) => value.ToString("0.0%", Italian);

    private static string Number(double value) => value.ToString("0.000", Italian);
}
