using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Shared.Runs;

// Contratti di /api/runs: elaborazioni dell'Agent viste dalla console (Fase 7) e dagli script della demo.

/// <summary>Richiesta di elaborazione manuale: senza articolo si prende l'ultimo articolo pubblico del cliente.</summary>
public sealed record StartRunRequest(long TicketId, long? ArticleId);

public sealed record StartRunResponse(string Outcome, int? RunId);

public sealed record RunSummary(
    int Id,
    int ZammadTicketId,
    int ZammadArticleId,
    RunTrigger Trigger,
    AgentRunState State,
    DateTimeOffset ReceivedAt,
    int? DurationMs,
    string? ErrorCode,
    string? Error,
    string? TraceId,
    DraftKind? DraftKind,
    double? Confidence);

public sealed record RunSourceView(byte Rank, ChunkSource Source, string Key, bool IsCitedByModel, bool IsVerified, string? Reason);

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
    DraftOutcome Outcome,
    IReadOnlyList<RunSourceView> Sources);

public sealed record RunDetail(RunSummary Run, RunDraftView? Draft);
