namespace Dusiburg.AI.Ticket.Shared.Domain;

// Enum del dominio con valori espliciti: ciascuno è anche una tabella di lookup del database (skill db-operations §4).

/// <summary>Modalità di erogazione del prodotto: nell'on-premise il cliente gestisce server, certificati e aggiornamenti.</summary>
public enum DeploymentMode : byte
{
    OnPrem = 1,
    Saas = 2
}

public enum SlaLevel : byte
{
    Base = 1,
    Plus = 2,
    Premium = 3
}

public enum TicketPriority : byte
{
    Low = 1,
    Normal = 2,
    High = 3
}

/// <summary>Stato di un ticket storico: solo i risolti hanno una soluzione da indicizzare.</summary>
public enum TicketState : byte
{
    Resolved = 1,
    Open = 2,
    Unresolved = 3
}

public enum MessageAuthor : byte
{
    Customer = 1,
    Agent = 2
}

public enum ReleaseNoteType : byte
{
    KnownIssue = 1,
    Fix = 2,
    Feature = 3
}

/// <summary>Origine di un frammento indicizzato: anche le note di rilascio sono una fonte (scenario 4).</summary>
public enum ChunkSource : byte
{
    HistoricalTicket = 1,
    KbArticle = 2,
    ReleaseNote = 3
}
