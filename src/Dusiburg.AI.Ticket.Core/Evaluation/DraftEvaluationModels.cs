using System.Text.Json.Serialization;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Core.Evaluation;

// Valutazione delle bozze (fase-8.md §2): letture salvate in JSON accanto al report, così il report si ricalcola con --replay e con
// i giudizi della griglia senza richiamare il modello.

/// <summary>Una citazione del modello: valida se la fonte era nel prompt, rilevante se è dello stesso caso del ticket.</summary>
public sealed record CitationRecord(string Id, bool IsVerified, bool IsRelevant);

/// <summary>Durata dei passi in millisecondi (il contesto viene dal database, non da Zammad).</summary>
public sealed record StepTimes(int Classification, int Retrieval, int Generation, int Verification)
{
    public int Total => Classification + Retrieval + Generation + Verification;
}

/// <summary>
/// Tutto ciò che la pipeline ha prodotto per un caso del test set, già ridotto a dati: classificazione, risposta del modello,
/// verifica, decisione del codice, testo che l'operatore avrebbe trovato nell'editor, tempi e token. <see cref="Error"/> se la
/// pipeline non è arrivata alla bozza (JSON non valido due volte, modello non raggiungibile).
/// </summary>
public sealed record DraftCaseRecord(
    string TicketNumber,
    string? CaseCode,
    TrapKind? Trap,
    string ProductCode,
    DeploymentMode Mode,
    string? Version,
    string Title,
    string CustomerMessage,
    string? Error,
    string? Category,
    string? Priority,
    IReadOnlyList<string> MissingInformation,
    bool SuspectedManipulation,
    bool CanAnswer,
    double? SelfAssessment,
    string ModelAnswer,
    DraftKind? Kind,
    double? Confidence,
    string? Reason,
    string? DraftText,
    IReadOnlyList<string> PromptSources,
    IReadOnlyList<string> RelevantPromptSources,
    IReadOnlyList<CitationRecord> Citations,
    IReadOnlyList<string> UnsupportedTerms,
    int TechnicalTerms,
    bool SignatureRemoved,
    int Attempts,
    long? InputTokens,
    long? OutputTokens,
    StepTimes? Timings)
{
    public bool Succeeded => Error is null;

    /// <summary>Il modello ha proposto una risposta (dichiarata possibile e non vuota), prima della decisione del codice.</summary>
    public bool ModelProposedAnswer => CanAnswer && !string.IsNullOrWhiteSpace(ModelAnswer);
}

/// <summary>Le letture di un giro di valutazione: parametri, casi, risorse. È il JSON salvato accanto al report.</summary>
public sealed record DraftEvaluationRun(
    DateTimeOffset RunAt,
    string Provider,
    string ClassifyModel,
    string DraftModel,
    double ConfidenceMinForDraft,
    TimeSpan Duration,
    IReadOnlyList<string> LoadedModels,
    IReadOnlyList<DraftCaseRecord> Cases);

/// <summary>Giudizio della griglia manuale (PLAN §5 Fase 8). "Utile" per il criterio del PLAN §1 sono i primi due.</summary>
public enum DraftJudgment
{
    [JsonStringEnumMemberName("corretta")] Correct,
    [JsonStringEnumMemberName("utile-con-modifiche")] UsefulWithEdits,
    [JsonStringEnumMemberName("da-scartare")] Discard
}

/// <summary>Una voce della griglia: il file si modifica a mano, quindi i nomi sono in italiano.</summary>
public sealed record DraftGrade(
    [property: JsonPropertyName("ticket")] string TicketNumber,
    [property: JsonPropertyName("giudizio")] DraftJudgment? Judgment,
    [property: JsonPropertyName("affermazioniNonSupportate")] int? UnsupportedClaims,
    [property: JsonPropertyName("nota")] string? Note,
    [property: JsonPropertyName("autore")] string? Author);

/// <summary>La griglia di un giro: il riferimento al file delle letture e una voce per caso.</summary>
public sealed record DraftGradeSheet(
    [property: JsonPropertyName("letture")] string RunFile,
    [property: JsonPropertyName("istruzioni")] string Instructions,
    [property: JsonPropertyName("voci")] IReadOnlyList<DraftGrade> Grades);
