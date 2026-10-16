using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Rag.Evaluation;

/// <summary>
/// Testo della query. <see cref="Full"/> è quello di produzione (oggetto e primo messaggio); <see cref="SymptomOnly"/> è la sola
/// variante di sintomo riservata al test set, mai presente nell'indice: toglie oggetto e dettagli, che i ticket simulati dello
/// stesso caso condividono, e misura la ricerca su una parafrasi vera.
/// </summary>
public enum EvalQueryKind
{
    Full,
    SymptomOnly
}

/// <summary>
/// Un ticket del test set come query: contesto del cliente preso dal ticket, testo pulito come in produzione, fonti rilevanti
/// dallo stesso caso della matrice (<c>CaseCode</c>): tutte in <see cref="Relevant"/>, articoli e note anche in
/// <see cref="RelevantDocuments"/>. Vuote per i trabocchetti senza caso atteso.
/// </summary>
public sealed record EvalCase(
    string TicketNumber,
    string? CaseCode,
    TrapKind? Trap,
    string ProductCode,
    DeploymentMode Mode,
    string? Version,
    string QueryText,
    string? SymptomText,
    IReadOnlySet<string> Relevant,
    IReadOnlySet<string> RelevantDocuments)
{
    public RetrievalQuery QueryFor(EvalQueryKind kind) => new(
        kind == EvalQueryKind.SymptomOnly ? SymptomText ?? QueryText : QueryText, ProductCode, Mode, Version);
}

/// <summary>Una configurazione confrontata: variante della classifica e penalità di versione.</summary>
public sealed record EvalVariant(string Name, RetrievalSettings Settings)
{
    public static IReadOnlyList<EvalVariant> All { get; } =
    [
        new("vettoriale", new RetrievalSettings(RetrievalVariant.Vector, ApplyVersionPenalty: false)),
        new("BM25", new RetrievalSettings(RetrievalVariant.Bm25, ApplyVersionPenalty: false)),
        new("ibrida (RRF)", new RetrievalSettings(RetrievalVariant.Hybrid, ApplyVersionPenalty: false)),
        new("vettoriale + penalità versione", new RetrievalSettings(RetrievalVariant.Vector, ApplyVersionPenalty: true)),
        new("ibrida + penalità versione (default)", RetrievalSettings.Default)
    ];
}

/// <summary>
/// Esito di un caso in una variante: le fonti in ordine, la posizione della prima rilevante (entro 10) e due misure sulle prime
/// <c>PROMPT_SOURCES_MAX</c> fonti, quelle che entreranno nel prompt.
/// </summary>
/// <param name="PromptPrecision">Quota di fonti rilevanti fra quelle del prompt.</param>
/// <param name="PromptHasDocument">Un articolo o una nota del caso fra le fonti del prompt; null se il caso non ne cita.</param>
public sealed record EvalCaseResult(
    EvalCase Case,
    IReadOnlyList<RetrievedSource> Sources,
    IReadOnlyList<RetrievedSource> PromptSources,
    int? FirstRelevantRank,
    double PromptPrecision,
    bool? PromptHasDocument,
    RetrievalTimings Timings)
{
    public bool HitAt(int k) => FirstRelevantRank is { } rank && rank <= k;

    public double ReciprocalRank => FirstRelevantRank is { } rank ? 1.0 / rank : 0;
}

public sealed record MetricSummary(int Cases, double HitAt1, double HitAt3, double HitAt5, double Mrr10, double PromptPrecision, double? PromptDocumentHit)
{
    public static MetricSummary Of(IReadOnlyCollection<EvalCaseResult> results)
    {
        double[] documents = [.. results.Where(r => r.PromptHasDocument is not null).Select(r => r.PromptHasDocument!.Value ? 1.0 : 0)];

        return new MetricSummary(
            results.Count,
            EvalMetrics.Mean(results.Select(r => r.HitAt(1) ? 1.0 : 0)),
            EvalMetrics.Mean(results.Select(r => r.HitAt(3) ? 1.0 : 0)),
            EvalMetrics.Mean(results.Select(r => r.HitAt(5) ? 1.0 : 0)),
            EvalMetrics.Mean(results.Select(r => r.ReciprocalRank)),
            EvalMetrics.Mean(results.Select(r => r.PromptPrecision)),
            documents.Length == 0 ? null : documents.Average());
    }
}

public sealed record VariantEvaluation(
    EvalQueryKind QueryKind,
    EvalVariant Variant,
    MetricSummary Overall,
    IReadOnlyDictionary<string, MetricSummary> ByGroup,
    double MeanLatencyMs,
    double P95LatencyMs,
    IReadOnlyList<EvalCaseResult> Results)
{
    public string Name => $"{Variant.Name}{(QueryKind == EvalQueryKind.SymptomOnly ? " · solo sintomo" : "")}";
}

/// <summary>Tutto ciò che finisce nel report: configurazione, indice, varianti per tipo di query, trabocchetti.</summary>
public sealed record RetrievalEvaluation(
    DateTimeOffset RunAt,
    string EmbeddingModel,
    RetrievalOptions Options,
    IReadOnlyDictionary<ChunkSource, int> IndexedChunks,
    int SkippedWithoutRelevant,
    IReadOnlyList<VariantEvaluation> Variants,
    IReadOnlyList<EvalCaseResult> Traps,
    IReadOnlyDictionary<string, string?> CaseOfTicket)
{
    /// <summary>La configurazione della pipeline sulla query di produzione: è il numero confrontato con la soglia del PLAN §1.</summary>
    public VariantEvaluation Default => Of(EvalQueryKind.Full);

    public VariantEvaluation Of(EvalQueryKind kind) =>
        Variants.Single(v => v.QueryKind == kind && v.Variant.Settings == RetrievalSettings.Default);
}
