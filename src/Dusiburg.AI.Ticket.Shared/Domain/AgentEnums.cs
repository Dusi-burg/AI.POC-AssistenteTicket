namespace Dusiburg.AI.Ticket.Shared.Domain;

/// <summary>Da dove è arrivato l'evento che ha avviato un'elaborazione (P1), o il comando manuale della demo.</summary>
public enum RunTrigger : byte
{
    Webhook = 1,
    Polling = 2,
    Manual = 3
}

public enum AgentRunState : byte
{
    Queued = 1,
    Running = 2,
    Completed = 3,
    Failed = 4,

    /// <summary>Evento da non elaborare (es. articolo non del cliente).</summary>
    Skipped = 5
}

/// <summary>Che cosa ha deciso il codice di pubblicare: la scelta non è del modello.</summary>
public enum DraftKind : byte
{
    Answer = 1,
    Clarification = 2,
    Escalation = 3
}

/// <summary>Che cosa ha fatto l'operatore della bozza (Fase 7).</summary>
public enum DraftOutcome : byte
{
    Unknown = 1,
    SentUnchanged = 2,
    SentEdited = 3,
    Discarded = 4
}
