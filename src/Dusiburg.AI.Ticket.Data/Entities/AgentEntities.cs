using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Data.Entities;

// Schema agent: elaborazioni, bozze, fonti citate ed esito del feedback. Il dettaglio si rifinisce nelle Fasi 6 e 7.

/// <summary>Elaborazione di un articolo del cliente: una sola per (ticket, articolo) di Zammad, è il vincolo di idempotenza (P2).</summary>
public sealed class AgentRun
{
    public int Id { get; set; }

    public int ZammadTicketId { get; set; }

    public int ZammadArticleId { get; set; }

    public RunTrigger Trigger { get; set; }

    public AgentRunState State { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public int? DurationMs { get; set; }

    public string? ErrorCode { get; set; }

    public string? Error { get; set; }

    /// <summary>Trace id di OpenTelemetry: il link alla traccia nel dashboard di Aspire.</summary>
    public string? TraceId { get; set; }

    public AgentDraft? Draft { get; set; }
}

/// <summary>
/// Esito della pipeline per un'elaborazione: classificazione e testo prodotti dal modello, segnali e confidenza calcolati dal
/// codice (P6), decisione del codice su cosa pubblicare, e poi che cosa ne ha fatto l'operatore.
/// </summary>
public sealed class AgentDraft
{
    public int Id { get; set; }

    public int AgentRunId { get; set; }

    public DraftKind Kind { get; set; }

    public string? Category { get; set; }

    public TicketPriority? Priority { get; set; }

    public required string Language { get; set; }

    public List<string> MissingInformation { get; set; } = [];

    /// <summary>Dichiarazione del modello (<c>puoRispondere</c>): un segnale, la decisione resta del codice.</summary>
    public bool CanAnswer { get; set; }

    /// <summary>Autovalutazione del modello, segnale minore e misurato in Fase 8.</summary>
    public double? SelfAssessment { get; set; }

    public double RetrievalScore { get; set; }

    public double CitationScore { get; set; }

    public double CoverageScore { get; set; }

    public double Confidence { get; set; }

    public required string Body { get; set; }

    public List<string> Questions { get; set; } = [];

    public required string ChatModel { get; set; }

    /// <summary>SHA-256 esadecimale del prompt inviato al modello.</summary>
    public required string PromptHash { get; set; }

    public DraftOutcome Outcome { get; set; } = DraftOutcome.Unknown;

    public string? FinalText { get; set; }

    /// <summary>Distanza di modifica normalizzata fra bozza e testo inviato (0 = invariato).</summary>
    public double? EditDistance { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? OutcomeAt { get; set; }

    public List<AgentDraftSource> Sources { get; } = [];
}

/// <summary>
/// Fonte proposta al modello per una bozza. <see cref="SourceKey"/> è la chiave di business (<c>Number</c> del ticket,
/// <c>Code</c> di articolo o nota): resta leggibile anche dopo una reindicizzazione, che rinnova i chunk.
/// </summary>
public sealed class AgentDraftSource
{
    public int Id { get; set; }

    public int AgentDraftId { get; set; }

    /// <summary>Posizione nel risultato del retrieval, da 1.</summary>
    public byte Rank { get; set; }

    public ChunkSource Source { get; set; }

    public required string SourceKey { get; set; }

    public int? ChunkId { get; set; }

    /// <summary>Motivo dichiarato dal modello quando cita la fonte.</summary>
    public string? Reason { get; set; }

    public bool IsCitedByModel { get; set; }

    /// <summary>La citazione corrisponde a una fonte davvero recuperata (verifica a valle).</summary>
    public bool IsVerified { get; set; }
}
