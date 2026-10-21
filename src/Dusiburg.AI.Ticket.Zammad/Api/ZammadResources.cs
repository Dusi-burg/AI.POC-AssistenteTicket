using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dusiburg.AI.Ticket.Zammad.Api;

// Risorse dell'API di Zammad lette dal POC: solo i campi usati. I nomi seguono l'API (snake_case via ZammadClient.Json).

/// <summary>Percorsi delle risorse sotto <c>/api/v1</c>.</summary>
public static class ZammadResources
{
    public const string Groups = "groups";
    public const string Users = "users";
    public const string Roles = "roles";
    public const string Organizations = "organizations";
    public const string ObjectAttributes = "object_manager_attributes";
    public const string Tags = "tag_list";
    public const string Webhooks = "webhooks";
    public const string Triggers = "triggers";
    public const string Settings = "settings";
    public const string Tickets = "tickets";
    public const string Articles = "ticket_articles";

    /// <summary>Connessioni ai provider dell'AI nativa (Fase 9a): nel 7.2 hanno sostituito la configurazione unica del provider.</summary>
    public const string AiProviderConnections = "ai/provider_connections";

    public const string AiAgents = "ai_agents";
}

public sealed record ZammadGroup(long Id, string Name, bool Active, bool? SharedDrafts);

public sealed record ZammadRole(long Id, string Name);

/// <summary>Utente come lo restituisce l'elenco (<c>GET /users</c>, senza <c>expand</c>): ruoli e gruppi per id.</summary>
public sealed record ZammadUserRecord(
    long Id,
    string Login,
    string? Email,
    string? Firstname,
    string? Lastname,
    long? OrganizationId,
    IReadOnlyList<long>? RoleIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? GroupIds);

public sealed record ZammadOrganization(long Id, string Name, string? Domain);

/// <summary>Attributo del gestore oggetti: <c>ToCreate</c>/<c>ToMigrate</c> veri finché non si eseguono le migrazioni.</summary>
public sealed record ZammadObjectAttribute(long Id, string Name, string Object, bool Active, bool ToCreate, bool ToMigrate);

public sealed record ZammadTag(long Id, string Name);

public sealed record ZammadWebhook(long Id, string Name, string Endpoint, string? SignatureToken, bool Active, string? CustomPayload);

public sealed record ZammadTrigger(long Id, string Name, bool Active, JsonElement Perform);

/// <summary>Impostazione: il valore corrente è in <c>state_current.value</c>.</summary>
public sealed record ZammadSetting(long Id, string Name, JsonElement StateCurrent)
{
    public bool BoolValue => StateCurrent.ValueKind == JsonValueKind.Object
        && StateCurrent.TryGetProperty("value", out JsonElement value)
        && value.ValueKind == JsonValueKind.True;
}

/// <summary>
/// Connessione a un provider AI: <c>config</c> porta <c>url</c> e <c>model</c>, <c>status</c> l'esito dell'ultima chiamata
/// (<c>state</c> ok/error, <c>message</c>). Una sola connessione serve la chat (<c>default_chat</c>).
/// </summary>
public sealed record ZammadAiProviderConnection(long Id, string Name, string Provider, JsonElement Config, bool DefaultChat, bool DefaultEmbedding, JsonElement Status)
{
    public string? ConfigValue(string name) =>
        Config.ValueKind == JsonValueKind.Object && Config.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public string? StatusValue(string name) =>
        Status.ValueKind == JsonValueKind.Object && Status.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Modelli che Zammad vede sull'endpoint del provider; <c>error</c> se non lo raggiunge.</summary>
public sealed record ZammadAiModelList(IReadOnlyList<JsonElement>? Models, string? Error);

/// <summary>Agente AI di Zammad: istruzioni, contesto e struttura del risultato in <c>definition</c>, mappatura sul ticket in <c>action_definition</c>.</summary>
public sealed record ZammadAiAgent(long Id, string Name, bool Active, string? AgentType, JsonElement Definition, JsonElement ActionDefinition);

/// <summary>Ticket con i campi personalizzati del POC (Fase 4): <see cref="RifStorico"/> è il numero del ticket storico importato.</summary>
public sealed record ZammadTicket(long Id, string Number, string Title, long GroupId, long? CustomerId, string? RifStorico);

public sealed record ZammadArticle(long Id, long TicketId, string? From, string? Sender, bool Internal, DateTimeOffset CreatedAt);

/// <summary>Token di accesso personale (solo i metadati: il valore si vede una volta, alla creazione).</summary>
public sealed record ZammadAccessToken(long Id, string Name);

internal sealed record ZammadAccessTokenList(IReadOnlyList<ZammadAccessToken> Tokens);

internal sealed record ZammadCreatedToken([property: JsonPropertyName("token")] string Value);
