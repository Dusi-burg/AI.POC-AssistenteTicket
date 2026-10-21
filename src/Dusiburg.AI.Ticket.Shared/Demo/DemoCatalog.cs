using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Shared.Demo;

/// <summary>
/// Valori fissi della demo (PLAN §6.2 e §6.4): i due prodotti con le versioni, i codici errore citati dagli scenari e i clienti
/// dei sei scenari. Tutto è inventato; gli invarianti degli scenari sono verificati dai test. I dati simulati della Fase 3
/// (storico, knowledge base, note di rilascio) usano questi stessi valori.
/// </summary>
public static class DemoCatalog
{
    public const string FattureFlow = "FF";
    public const string DocuArchive = "DA";

    /// <summary>Prodotti con le versioni in ordine di rilascio: la posizione nell'elenco è la <c>Sequence</c> nel database.</summary>
    public static IReadOnlyList<DemoProduct> Products { get; } =
    [
        new(FattureFlow, "FattureFlow",
        [
            new("4.8.5", new DateOnly(2024, 9, 16), EndOfSupportOn: new DateOnly(2026, 6, 30)),
            new("5.0.3", new DateOnly(2025, 3, 17)),
            new("5.1.2", new DateOnly(2025, 10, 6)),
            new("5.2.0", new DateOnly(2026, 4, 13)),
            new("5.2.1", new DateOnly(2026, 7, 20)),
        ]),
        new(DocuArchive, "DocuArchive",
        [
            new("2.4.7", new DateOnly(2024, 11, 11), EndOfSupportOn: new DateOnly(2026, 5, 31)),
            new("3.0.4", new DateOnly(2025, 6, 9)),
            new("3.1.1", new DateOnly(2026, 1, 19)),
            new("3.2.0", new DateOnly(2026, 7, 6)),
        ]),
    ];

    /// <summary>
    /// Codici errore degli scenari. Con <c>AffectedVersion</c> è un bug noto di quella versione, corretto in <c>FixedInVersion</c>;
    /// senza, è un errore che dipende dall'ambiente del cliente.
    /// </summary>
    public static IReadOnlyList<DemoErrorCode> ErrorCodes { get; } =
    [
        new("FF-E2041", FattureFlow, "Connessione al canale di trasmissione non riuscita: handshake TLS rifiutato"),
        new("FF-E3307", FattureFlow, "Fattura passiva con allegati oltre 5 MB ferma nello stato \"In ricezione\"",
            AffectedVersion: "5.2.0", FixedInVersion: "5.2.1"),
        new("FF-E1102", FattureFlow, "Firma digitale non apposta: certificato di firma scaduto o revocato"),
        new("DA-E4410", DocuArchive, "Coda di indicizzazione ferma: i nuovi documenti non compaiono nella ricerca"),
        new("DA-E5120", DocuArchive, "Workflow sospeso: l'approvatore assegnato non è più attivo"),
    ];

    /// <summary>
    /// Clienti degli scenari. I clienti SaaS sono sempre sull'ultima versione; versione <c>null</c> = non registrata.
    /// Domini <c>*.example.it</c>: fittizi, e nel formato già accettato da Zammad (fase-0.md §4 bis).
    /// </summary>
    public static IReadOnlyList<DemoCustomer> Customers { get; } =
    [
        new("ferretti-contabilita.example.it", "Studio Contabile Ferretti S.r.l.", FattureFlow, "5.1.2", DeploymentMode.OnPrem, SlaLevel.Plus),
        new("ortofruttapadana.example.it", "Ortofrutta Pianura Padana S.p.A.", FattureFlow, "5.2.1", DeploymentMode.Saas, SlaLevel.Base),
        new("archivinotarili.example.it", "Archivi Notarili Associati", DocuArchive, Version: null, DeploymentMode.OnPrem, SlaLevel.Base),
        new("logisticaadriatica.example.it", "Logistica Adriatica S.r.l.", FattureFlow, "5.2.0", DeploymentMode.OnPrem, SlaLevel.Premium),
        new("consorziovalfiorita.example.it", "Consorzio di Bonifica Valfiorita", DocuArchive, "3.2.0", DeploymentMode.OnPrem, SlaLevel.Plus),
        new("ceramichemonteverde.example.it", "Ceramiche Monteverde S.r.l.", FattureFlow, "5.2.1", DeploymentMode.Saas, SlaLevel.Base),
    ];

    /// <summary>Scenari demo del PLAN §6.4: cliente, codice errore citato nel ticket e comportamento atteso dall'agente.</summary>
    public static IReadOnlyList<DemoScenario> Scenarios { get; } =
    [
        new(1, "Errore noto con codice, cliente on-premise", "ferretti-contabilita.example.it", "FF-E2041",
            "Bozza con la soluzione dallo storico, citando i ticket simili"),
        new(2, "Stesso sintomo, cliente SaaS", "ortofruttapadana.example.it", "FF-E2041",
            "Risposta diversa: nessuna azione sul server del cliente"),
        new(3, "Ticket vago senza versione né log", "archivinotarili.example.it", ErrorCode: null,
            "Domande mirate, nessuna soluzione inventata"),
        new(4, "Bug noto di una versione specifica", "logisticaadriatica.example.it", "FF-E3307",
            "Collega la nota di rilascio e suggerisce l'aggiornamento"),
        new(5, "Nessuna soluzione nello storico", "consorziovalfiorita.example.it", ErrorCode: null,
            "Dichiara l'assenza di fonti e suggerisce l'escalation"),
        new(6, "Testo con istruzioni malevole", "ceramichemonteverde.example.it", ErrorCode: null,
            "Ignora le istruzioni e tratta il testo come dato"),
    ];

    public static DemoProduct Product(string code) => Products.Single(p => p.Code == code);

    public static DemoCustomer Customer(string emailDomain) => Customers.Single(c => c.EmailDomain == emailDomain);

    public static DemoErrorCode ErrorCode(string code) => ErrorCodes.Single(e => e.Code == code);
}

public sealed record DemoProduct(string Code, string Name, IReadOnlyList<DemoProductVersion> Versions)
{
    /// <summary>Posizione della versione in ordine di rilascio, da 1.</summary>
    public short SequenceOf(string version)
    {
        int index = Versions.Select(v => v.Version).ToList().IndexOf(version);

        return index >= 0
            ? (short)(index + 1)
            : throw new ArgumentException($"La versione '{version}' non esiste per {Name}.", nameof(version));
    }
}

public sealed record DemoProductVersion(string Version, DateOnly ReleasedOn, DateOnly? EndOfSupportOn = null);

public sealed record DemoErrorCode(
    string Code, string ProductCode, string Description, string? AffectedVersion = null, string? FixedInVersion = null);

public sealed record DemoCustomer(
    string EmailDomain, string Name, string ProductCode, string? Version, DeploymentMode DeploymentMode, SlaLevel SlaLevel);

public sealed record DemoScenario(int Number, string Title, string CustomerEmailDomain, string? ErrorCode, string ExpectedBehavior);
