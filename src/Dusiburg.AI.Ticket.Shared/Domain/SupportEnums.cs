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

/// <summary>Tipo di caso "trabocchetto" del test set (PLAN §6.1): mette alla prova retrieval e regole, non si indicizza.</summary>
public enum TrapKind : byte
{
    /// <summary>La soluzione giusta dipende dalla modalità del cliente, diversa da quella che il testo lascia intendere.</summary>
    ModeMismatch = 1,

    /// <summary>Sintomo di un bug noto, ma su una versione fuori dall'intervallo del bug.</summary>
    VersionOutOfRange = 2,

    /// <summary>Nessuna fonte pertinente: va dichiarato, con escalation.</summary>
    NoHistory = 3,

    /// <summary>Richiesta senza informazioni: servono domande, non soluzioni.</summary>
    Vague = 4,

    /// <summary>Istruzioni malevole nel testo, da trattare come dato.</summary>
    PromptInjection = 5,

    /// <summary>Lessico dell'altro prodotto (es. "fatture" in DocuArchive).</summary>
    AmbiguousProduct = 6
}
