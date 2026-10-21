using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Shared.Runs;

// Contratti di /api/runs e /api/outcomes: elaborazioni ed esiti dell'Agent visti dalla console (Fase 7) e dagli script della demo.

/// <summary>Richiesta di elaborazione manuale: senza articolo si prende l'ultimo articolo pubblico del cliente.</summary>
public sealed record StartRunRequest(long TicketId, long? ArticleId);

public sealed record StartRunResponse(string Outcome, int? RunId);

/// <summary>Filtri dell'elenco delle elaborazioni; tutti facoltativi.</summary>
public sealed record RunFilter(AgentRunState? State, DraftKind? Kind, DraftOutcome? Outcome);

public sealed record RunSummary(
    int Id,
    int ZammadTicketId,
    int ZammadArticleId,
    string? TicketNumber,
    string? TicketTitle,
    RunTrigger Trigger,
    AgentRunState State,
    DateTimeOffset ReceivedAt,
    int? DurationMs,
    string? ErrorCode,
    string? Error,
    string? TraceId,
    DraftKind? DraftKind,
    double? Confidence,
    DraftOutcome? Outcome,
    PipelineMode Mode = PipelineMode.Fixed);

public sealed record RunSourceView(byte Rank, ChunkSource Source, string Key, bool IsCitedByModel, bool IsVerified, string? Reason);

/// <summary>Durata dei passi della pipeline in millisecondi.</summary>
public sealed record RunTimings(int Context, int Classification, int Retrieval, int Generation, int Verification, int Publication)
{
    public int Total => Context + Classification + Retrieval + Generation + Verification + Publication;
}

/// <summary>Che cosa ha fatto l'operatore della bozza (Fase 7); i campi della risposta sono nulli finché non c'è.</summary>
public sealed record RunOutcomeView(
    DraftOutcome Outcome,
    DateTimeOffset? OutcomeAt,
    string? Reason,
    int? ReplyArticleId,
    string? FinalText,
    double? EditDistance,
    bool? DisclosureKept);

public sealed record RunDraftView(
    DraftKind Kind,
    string? Category,
    TicketPriority? Priority,
    IReadOnlyList<string> MissingInformation,
    bool CanAnswer,
    double? SelfAssessment,
    double RetrievalScore,
    double CitationScore,
    double CoverageScore,
    double Confidence,
    string Body,
    IReadOnlyList<string> Questions,
    string ChatModel,
    RunTimings Timings,
    RunOutcomeView Outcome,
    IReadOnlyList<RunSourceView> Sources);

/// <summary>Una chiamata a un tool della modalità agente (Fase 9b).</summary>
public sealed record RunToolCallView(byte Sequence, string ToolName, string Arguments, string Outcome, int DurationMs, string? Message);

public sealed record RunDetail(RunSummary Run, RunDraftView? Draft, IReadOnlyList<RunToolCallView>? ToolCalls = null);

/// <summary>Richiesta di controllo manuale dell'esito delle bozze di un ticket.</summary>
public sealed record CheckOutcomeRequest(long TicketId);

public sealed record CheckedOutcome(int RunId, DraftOutcome Outcome, double? EditDistance, bool? DisclosureKept, IReadOnlyList<string> Tags, string Reason);

public sealed record CheckOutcomeResponse(long TicketId, IReadOnlyList<CheckedOutcome> Resolved);

/// <summary>Conteggio delle bozze per tipo ed esito.</summary>
public sealed record OutcomeCount(DraftKind Kind, DraftOutcome Outcome, int Count);

/// <summary>Una risposta inviata senza la dicitura AI (P8): da mostrare come avviso.</summary>
public sealed record DisclosureRemoval(int RunId, int ZammadTicketId, string? TicketNumber, int? ReplyArticleId, DateTimeOffset? OutcomeAt, DraftOutcome Outcome);

/// <summary>
/// Riepilogo degli esiti (pagina Esiti). <see cref="Useful"/>: bozze inviate com'erano o con una distanza fino a
/// <see cref="UsefulMaxEditDistance"/> (criterio "invio diretto o con piccole modifiche" del PLAN §1), sulle bozze con un esito.
/// </summary>
public sealed record OutcomeSummary(
    int Drafts,
    int Pending,
    IReadOnlyList<OutcomeCount> Counts,
    double? AverageEditDistance,
    int Useful,
    int Decided,
    double UsefulMaxEditDistance,
    double FeedbackMaxEditDistance,
    IReadOnlyList<DisclosureRemoval> DisclosureRemovals);
