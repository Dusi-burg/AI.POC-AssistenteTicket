using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Data.Entities;

// Schema support: il "gestionale" simulato con prodotti, clienti, storico dei ticket, knowledge base e note di rilascio.

public sealed class Product
{
    public short Id { get; set; }

    /// <summary>Chiave di business (<c>FF</c>, <c>DA</c>).</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    public List<ProductVersion> Versions { get; } = [];
}

public sealed class ProductVersion
{
    public int Id { get; set; }

    public short ProductId { get; set; }

    public Product Product { get; set; } = null!;

    public required string Version { get; set; }

    /// <summary>Ordine di rilascio nel prodotto, da 1: gli intervalli di applicabilità si confrontano su questo numero.</summary>
    public short Sequence { get; set; }

    public DateOnly ReleasedOn { get; set; }

    public DateOnly? EndOfSupportOn { get; set; }
}

public sealed class Customer
{
    public int Id { get; set; }

    /// <summary>Chiave di business: il dominio email con cui si riconosce il cliente di un ticket (P4).</summary>
    public required string EmailDomain { get; set; }

    public required string Name { get; set; }

    public short ProductId { get; set; }

    /// <summary>Versione installata o in uso; <c>null</c> se il cliente non l'ha mai comunicata.</summary>
    public int? ProductVersionId { get; set; }

    public DeploymentMode DeploymentMode { get; set; }

    public SlaLevel SlaLevel { get; set; }

    public int? ZammadOrganizationId { get; set; }
}

public sealed class HistoricalTicket
{
    public int Id { get; set; }

    /// <summary>Chiave di business: l'id con cui il modello cita il ticket fra le fonti (PLAN §7.2).</summary>
    public required string Number { get; set; }

    public int CustomerId { get; set; }

    /// <summary>Referente del cliente che ha aperto il ticket: diventa l'utente cliente in Zammad (seed della Fase 4).</summary>
    public required string ContactName { get; set; }

    public required string ContactEmail { get; set; }

    public short ProductId { get; set; }

    public int? ProductVersionId { get; set; }

    public DeploymentMode DeploymentMode { get; set; }

    public required string Category { get; set; }

    public TicketPriority Priority { get; set; }

    public TicketState State { get; set; }

    public required string Subject { get; set; }

    public required string Description { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Ticket del test set: escluso dall'indice, usato solo per la valutazione.</summary>
    public bool IsTestSet { get; set; }

    /// <summary>Valorizzato per i ticket importati anche in Zammad (Fase 4).</summary>
    public int? ZammadTicketId { get; set; }

    /// <summary>
    /// Caso della matrice da cui nasce il ticket simulato: è la verità di riferimento della valutazione (le fonti pertinenti sono
    /// quelle dello stesso caso). Per un trabocchetto è il caso atteso, <c>null</c> se nessuno lo è.
    /// </summary>
    public string? CaseCode { get; set; }

    /// <summary>Solo nel test set: il tipo di trabocchetto.</summary>
    public TrapKind? TrapKind { get; set; }

    /// <summary>Ticket di cui questo è il duplicato (chiuso senza soluzione propria).</summary>
    public int? DuplicateOfId { get; set; }

    public List<TicketMessage> Messages { get; } = [];

    public Resolution? Resolution { get; set; }
}

public sealed class TicketMessage
{
    public int Id { get; set; }

    public int HistoricalTicketId { get; set; }

    /// <summary>Posizione nel thread, da 1.</summary>
    public short Sequence { get; set; }

    public MessageAuthor Author { get; set; }

    public required string Body { get; set; }

    public DateTimeOffset SentAt { get; set; }
}

public sealed class Resolution
{
    public int Id { get; set; }

    public int HistoricalTicketId { get; set; }

    public string? Cause { get; set; }

    public required string Solution { get; set; }

    /// <summary>Soluzione confermata dal cliente; quelle non verificate restano nello storico come rumore realistico.</summary>
    public bool IsVerified { get; set; }
}

public sealed class KbArticle
{
    public int Id { get; set; }

    /// <summary>Chiave di business: l'id con cui il modello cita l'articolo.</summary>
    public required string Code { get; set; }

    public short ProductId { get; set; }

    /// <summary><c>null</c> = vale per entrambe le modalità.</summary>
    public DeploymentMode? DeploymentMode { get; set; }

    /// <summary>Prima versione a cui si applica; <c>null</c> = da sempre.</summary>
    public int? VersionFromId { get; set; }

    /// <summary>Ultima versione a cui si applica; <c>null</c> = ancora valido.</summary>
    public int? VersionToId { get; set; }

    public required string Title { get; set; }

    public required string Body { get; set; }

    public DateOnly UpdatedOn { get; set; }
}

public sealed class ReleaseNote
{
    public int Id { get; set; }

    /// <summary>Chiave di business: l'id con cui il modello cita la nota.</summary>
    public required string Code { get; set; }

    public int ProductVersionId { get; set; }

    public ReleaseNoteType Type { get; set; }

    public required string Title { get; set; }

    public required string Body { get; set; }
}
