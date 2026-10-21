using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Data.Entities;

// Schema agent: elaborazioni, bozze, fonti citate ed esito del feedback (Fasi 6 e 7).

/// <summary>Elaborazione di un articolo del cliente: una sola per (ticket, articolo) di Zammad, è il vincolo di idempotenza (P2).</summary>
public sealed class AgentRun
{
    public int Id { get; set; }

    public int ZammadTicketId { get; set; }

    public int ZammadArticleId { get; set; }

    /// <summary>Numero e titolo del ticket, letti al passo "contesto": la console li mostra senza chiedere a Zammad.</summary>
    public string? TicketNumber { get; set; }

    public string? TicketTitle { get; set; }

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

    /// <summary>Pipeline fissa o agente con i tool (Fase 9b): <c>PIPELINE_MODE</c> al momento dell'elaborazione.</summary>
    public PipelineMode Mode { get; set; } = PipelineMode.Fixed;

    public AgentDraft? Draft { get; set; }

    /// <summary>Chiamate ai tool della modalità agente, nell'ordine; vuoto con la pipeline fissa.</summary>
    public List<AgentToolCall> ToolCalls { get; } = [];
}

/// <summary>
/// Una chiamata a un tool del server MCP nella modalità agente (Fase 9b): argomenti come li ha passati il modello più quelli
/// imposti dal codice, esito (<c>ok</c>, <c>error:&lt;codice&gt;</c>, <c>rejected:&lt;codice&gt;</c> se l'ha fermata la guardia).
/// </summary>
public sealed class AgentToolCall
{
    public int Id { get; set; }

    public int AgentRunId { get; set; }

    /// <summary>Ordine della chiamata nel run, da 1.</summary>
    public byte Sequence { get; set; }

    public required string ToolName { get; set; }

    /// <summary>Argomenti in JSON.</summary>
    public required string Arguments { get; set; }

    public required string Outcome { get; set; }

    public int DurationMs { get; set; }

    /// <summary>Messaggio d'errore o del rifiuto; <c>null</c> se la chiamata è riuscita.</summary>
    public string? Message { get; set; }
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

    /// <summary>Durata dei passi della pipeline (gli stessi span del dashboard di Aspire).</summary>
    public int ContextMs { get; set; }

    public int ClassificationMs { get; set; }

    public int RetrievalMs { get; set; }

    public int GenerationMs { get; set; }

    public int VerificationMs { get; set; }

    public int PublicationMs { get; set; }

    /// <summary>
    /// Nota interna pubblicata con la bozza: la risposta dell'operatore è il primo articolo pubblico con un id maggiore (Fase 7).
    /// Gli id degli articoli crescono, gli orologi di Windows e del container no.
    /// </summary>
    public int NoteArticleId { get; set; }

    public DraftOutcome Outcome { get; set; } = DraftOutcome.Unknown;

    /// <summary>Articolo inviato dall'operatore a partire dalla bozza; <c>null</c> se non ha risposto.</summary>
    public int? ReplyArticleId { get; set; }

    /// <summary>Testo semplice dell'articolo inviato.</summary>
    public string? FinalText { get; set; }

    /// <summary>Distanza di modifica a parole fra bozza e testo inviato, senza la dicitura (0 = invariato, 1 = tutto diverso).</summary>
    public double? EditDistance { get; set; }

    /// <summary>L'articolo inviato contiene la dicitura AI (P8); <c>null</c> se non c'è risposta.</summary>
    public bool? DisclosureKept { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? OutcomeAt { get; set; }

    /// <summary>Perché l'esito è quello: distanza oltre la soglia, bozza superata, ticket chiuso senza risposta.</summary>
    public string? OutcomeReason { get; set; }

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
