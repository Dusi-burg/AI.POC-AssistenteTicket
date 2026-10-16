using System.Text.Json;
using System.Text.Json.Serialization;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Data.Seed;

// Forma dei file di data/ (fase-3.md §1). Scritti a mano da Claude Code, salvo tickets.json che è l'uscita del generatore.

public sealed record CustomersFile(IReadOnlyList<CustomerDefinition> Customers);

public sealed record CustomerDefinition(
    string EmailDomain, string Name, string Product, string? Version, DeploymentMode Mode, SlaLevel Sla);

public sealed record ReleaseNotesFile(IReadOnlyList<ReleaseNoteDefinition> ReleaseNotes);

public sealed record ReleaseNoteDefinition(string Code, string Product, string Version, ReleaseNoteType Type, string Title, string Body);

public sealed record KbFile(IReadOnlyList<KbArticleDefinition> Articles);

/// <summary>Articolo della KB; con più prodotti è un modello: <c>{P}</c> nel codice e nei codici errore, <c>{prodotto}</c> nel testo.</summary>
public sealed record KbArticleDefinition(
    string Code,
    IReadOnlyList<string> Products,
    DeploymentMode? Mode,
    string? VersionFrom,
    string? VersionTo,
    DateOnly UpdatedOn,
    string Title,
    IReadOnlyList<KbSection> Sections);

public sealed record KbSection(string Title, string Body);

public sealed record CasesFile(IReadOnlyList<CaseDefinition> Cases);

/// <summary>
/// Un caso della matrice: prodotto × modalità × intervallo di versioni × causa × soluzione, con le varianti di testo. L'ultima
/// variante di <see cref="Symptoms"/> è riservata al test set. <see cref="NotBefore"/> limita i ticket a dopo un evento esterno
/// (es. il gateway che dismette una suite di cifratura).
/// </summary>
public sealed record CaseDefinition(
    string Code,
    IReadOnlyList<string> Products,
    IReadOnlyList<DeploymentMode> Modes,
    PerProductValue VersionFrom,
    PerProductValue VersionTo,
    string Category,
    TicketPriority Priority,
    string? ErrorCode,
    int Weight,
    IReadOnlyList<string> Subjects,
    IReadOnlyList<string> Symptoms,
    IReadOnlyList<string> Details,
    IReadOnlyList<string> Questions,
    IReadOnlyList<string> FollowUps,
    string Cause,
    IReadOnlyList<string> Solutions,
    IReadOnlyList<string> Replies,
    IReadOnlyList<string> Kb,
    IReadOnlyList<string> ReleaseNotes,
    DateOnly? NotBefore = null)
{
    public IReadOnlyList<string> IndexedSymptoms => Symptoms.Take(Symptoms.Count - 1).ToList();

    public string ReservedSymptom => Symptoms[^1];
}

public sealed record NoiseFile(
    IReadOnlyList<string> CustomerOpeners,
    IReadOnlyList<string> CustomerClosers,
    IReadOnlyList<string> Signatures,
    IReadOnlyList<string> Disclaimers,
    IReadOnlyList<string> AttachmentMentions,
    IReadOnlyList<string> PastedSecrets,
    IReadOnlyList<string> CustomerThanks,
    IReadOnlyList<string> CustomerFollowUpPrefixes,
    IReadOnlyList<string> AgentGreetings,
    IReadOnlyList<string> AgentInfoRequests,
    IReadOnlyList<string> AgentGenericQuestions,
    IReadOnlyList<string> AgentClosings,
    IReadOnlyList<string> AgentQuestionClosings,
    IReadOnlyList<string> AgentDuplicateReplies,
    IReadOnlyList<string> AgentUnresolvedReplies,
    IReadOnlyList<string> QuoteHeaders,
    IReadOnlyList<string> Operators,
    IReadOnlyList<string> FirstNames,
    IReadOnlyList<string> LastNames,
    IReadOnlyList<string> Roles);

public sealed record TrapsFile(IReadOnlyList<TrapDefinition> Traps);

/// <summary>Ticket del test set scritto per intero; <see cref="ExpectedCaseCode"/> <c>null</c> = nessun caso è la risposta giusta.</summary>
public sealed record TrapDefinition(
    TrapKind TrapKind, string Customer, DateTimeOffset OpenedAt, string Subject, string Body, string? ExpectedCaseCode, string ExpectedBehavior);

/// <summary>Uscita del generatore (<c>data/tickets.json</c>), caricata da <c>DbInit</c>.</summary>
public sealed record TicketsFile(int RandomSeed, IReadOnlyList<GeneratedTicket> Tickets);

public sealed record GeneratedTicket(
    string Number,
    string Customer,
    string Product,
    string? Version,
    DeploymentMode Mode,
    string Category,
    TicketPriority Priority,
    TicketState State,
    string Subject,
    string Description,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    bool IsTestSet,
    string? CaseCode,
    TrapKind? TrapKind,
    string? DuplicateOf,
    IReadOnlyList<GeneratedMessage> Messages,
    GeneratedResolution? Resolution);

public sealed record GeneratedMessage(MessageAuthor Author, string Body, DateTimeOffset SentAt);

public sealed record GeneratedResolution(string? Cause, string Solution, bool IsVerified);

/// <summary>Valore uguale per tutti i prodotti (stringa o <c>null</c> nel JSON) oppure diverso per prodotto (oggetto).</summary>
[JsonConverter(typeof(PerProductValueConverter))]
public sealed class PerProductValue
{
    private readonly string? _common;
    private readonly IReadOnlyDictionary<string, string?>? _byProduct;

    public static PerProductValue None { get; } = new(null, null);

    private PerProductValue(string? common, IReadOnlyDictionary<string, string?>? byProduct)
    {
        _common = common;
        _byProduct = byProduct;
    }

    public static PerProductValue Common(string? value) => new(value, null);

    public static PerProductValue ByProduct(IReadOnlyDictionary<string, string?> values) => new(null, values);

    public string? For(string product) => _byProduct is null ? _common : _byProduct.GetValueOrDefault(product);

    private sealed class PerProductValueConverter : JsonConverter<PerProductValue>
    {
        public override bool HandleNull => true;

        public override PerProductValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.Null => None,
            JsonTokenType.String => Common(reader.GetString()),
            JsonTokenType.StartObject => ByProduct(JsonSerializer.Deserialize<Dictionary<string, string?>>(ref reader, options)!),
            _ => throw new JsonException("Atteso null, una stringa o un oggetto { prodotto: versione }.")
        };

        public override void Write(Utf8JsonWriter writer, PerProductValue value, JsonSerializerOptions options)
        {
            if (value._byProduct is not null)
            {
                JsonSerializer.Serialize(writer, value._byProduct, options);
            }
            else
            {
                writer.WriteStringValue(value._common);
            }
        }
    }
}
