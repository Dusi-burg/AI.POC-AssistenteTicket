using System.Text;
using Dusiburg.AI.Ticket.Shared.Demo;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Cli.Zammad;

/// <summary>Campo personalizzato da creare con il gestore oggetti di Zammad.</summary>
internal sealed record AttributeDefinition(string Object, string Name, string Display, string DataType, object DataOption, object Screens, int Position);

/// <summary>Trigger che chiama il webhook dell'Agent: condizioni nel formato dell'API di Zammad.</summary>
internal sealed record TriggerDefinition(string Name, Dictionary<string, object> Condition, string Note);

/// <summary>
/// Cosa contiene l'helpdesk del POC (fase-4.md §2): gruppi, campi personalizzati, tag, utenti tecnici, webhook e trigger. Un solo
/// posto per i nomi, che bootstrap, seed e demo usano allo stesso modo.
/// </summary>
internal static class ZammadDirectory
{
    public const string SupportGroup = "Supporto";
    public const string InvoicingGroup = "Fatturazione elettronica";
    public const string DocumentsGroup = "Documentale";

    public static IReadOnlyList<string> Groups { get; } = [SupportGroup, InvoicingGroup, DocumentsGroup];

    public const string AgentRole = "Agent";
    public const string CustomerRole = "Customer";

    /// <summary>Utente tecnico con cui l'Agent scrive bozze, note e tag (PLAN §4: lo storico mostra chi ha fatto cosa).</summary>
    public const string AssistantLogin = "ai-assistant";
    public const string AssistantEmail = "ai-assistant@dusiburg-software.example.it";

    public const string TokenName = "assistente-ticket";

    /// <summary>Bozze condivise, articoli, tag: tutto sotto <c>ticket.agent</c>.</summary>
    public static IReadOnlyList<string> TokenPermissions { get; } = ["ticket.agent"];

    public const string OperatorDomain = "dusiburg-software.example.it";

    /// <summary>P3 (bozza, bassa confidenza) e P8 (contenuto generato da AI).</summary>
    public static IReadOnlyList<string> Tags { get; } =
        ["ai-bozza", "ai-bassa-confidenza", "ai-generata", "ai-generata-in-parte", "ai-dicitura-rimossa"];

    public const string WebhookName = "Assistente ticket";
    public const string TriggerName = "Assistente ticket — articolo del cliente";
    public const string ReplyTriggerName = "Assistente ticket — risposta dell'operatore";
    public const string ClosedTriggerName = "Assistente ticket — ticket chiuso";

    /// <summary>I trigger del webhook: <c>zammad trigger on|off</c> li accende e spegne insieme.</summary>
    public static IReadOnlyList<string> TriggerNames { get; } = [TriggerName, ReplyTriggerName, ClosedTriggerName];

    /// <summary>
    /// Payload minimale (fase-0.md §4 ter): JSON valido prima dell'interpolazione, quindi variabili tra virgolette. Con
    /// <c>ticket_state</c> l'Agent riconosce la chiusura, che non ha articolo (Fase 7).
    /// </summary>
    public const string WebhookPayload =
        """{ "ticket_id": "#{ticket.id}", "ticket_number": "#{ticket.number}", "ticket_state": "#{ticket.state.name}", "article_id": "#{article.id}", "article_sender": "#{article.sender.name}", "article_internal": "#{article.internal}" }""";

    /// <summary>Id fissi dei mittenti predefiniti di Zammad.</summary>
    public const int AgentSenderId = 1;
    public const int CustomerSenderId = 2;

    /// <summary>Id fisso dello stato predefinito <c>closed</c>.</summary>
    public const int ClosedStateId = 4;

    /// <summary>
    /// Trigger del webhook, tutti sui gruppi del POC. Articolo del cliente: parte l'elaborazione. Articolo dell'operatore (anche le
    /// note interne dell'assistente: le scarta l'Agent) e chiusura: controllo dell'esito della bozza (Fase 7).
    /// </summary>
    public static IReadOnlyList<TriggerDefinition> Triggers(long[] groupIds) =>
    [
        new(TriggerName, ArticleCreatedBy(CustomerSenderId, groupIds),
            "Assistente ticket (POC): articoli del cliente, per evitare cicli con le scritture dell'agente"),
        new(ReplyTriggerName, ArticleCreatedBy(AgentSenderId, groupIds),
            "Assistente ticket (POC): risposte dell'operatore, per l'esito della bozza e la marcatura AI"),
        new(ClosedTriggerName, new Dictionary<string, object>
            {
                ["ticket.state_id"] = new { Operator = "is", Value = new[] { ClosedStateId } },
                ["ticket.group_id"] = new { Operator = "is", Value = groupIds },
            },
            "Assistente ticket (POC): ticket chiuso, per le bozze rimaste senza risposta")
    ];

    private static Dictionary<string, object> ArticleCreatedBy(int senderId, long[] groupIds) => new()
    {
        ["article.action"] = new { Operator = "is", Value = "create" },
        ["article.sender_id"] = new { Operator = "is", Value = new[] { senderId } },
        ["ticket.group_id"] = new { Operator = "is", Value = groupIds },
    };

    public const string ProductField = "prodotto";
    public const string VersionField = "versione_prodotto";
    public const string ModeField = "modalita";
    public const string SlaField = "livello_sla";
    public const string HistoricalNumberField = "rif_storico";

    public static IReadOnlyList<AttributeDefinition> Attributes { get; } =
    [
        new("Organization", ProductField, "Prodotto", "select", Select(ProductOptions()), OrganizationScreens(), 1500),
        new("Organization", VersionField, "Versione del prodotto", "input", Input(20), OrganizationScreens(), 1510),
        new("Organization", ModeField, "Modalità", "select", Select(new() { ["onprem"] = "On-premise", ["saas"] = "SaaS" }), OrganizationScreens(), 1520),
        new("Organization", SlaField, "Livello SLA", "select", Select(new() { ["base"] = "Base", ["plus"] = "Plus", ["premium"] = "Premium" }), OrganizationScreens(), 1530),
        new("Ticket", ProductField, "Prodotto", "select", Select(ProductOptions()), TicketScreens(), 1500),
        new("Ticket", VersionField, "Versione del prodotto", "input", Input(20), TicketScreens(), 1510),
        new("Ticket", HistoricalNumberField, "Riferimento storico", "input", Input(20), TicketScreens(), 1520),
    ];

    public static string GroupFor(string productCode) => productCode switch
    {
        DemoCatalog.FattureFlow => InvoicingGroup,
        DemoCatalog.DocuArchive => DocumentsGroup,
        _ => SupportGroup
    };

    public static string ModeValue(DeploymentMode mode) => mode == DeploymentMode.OnPrem ? "onprem" : "saas";

    public static string SlaValue(SlaLevel sla) => sla.ToString().ToLowerInvariant();

    /// <summary>Email (e login) di un operatore dal nome: <c>Marta Ferrari</c> → <c>marta.ferrari@dusiburg-software.example.it</c>.</summary>
    public static string OperatorEmail(string fullName) => PersonEmail(fullName, OperatorDomain);

    /// <summary><c>nome.cognome@dominio</c>, senza accenti né spazi.</summary>
    public static string PersonEmail(string fullName, string domain) =>
        $"{string.Join('.', fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Slug))}@{domain}";

    public static (string First, string Last) SplitName(string fullName)
    {
        int space = fullName.IndexOf(' ', StringComparison.Ordinal);

        return space < 0 ? (fullName, string.Empty) : (fullName[..space], fullName[(space + 1)..]);
    }

    private static string Slug(string part) =>
        new(part.ToLowerInvariant().Normalize(NormalizationForm.FormD).Where(char.IsAsciiLetterLower).ToArray());

    private static Dictionary<string, string> ProductOptions() =>
        DemoCatalog.Products.ToDictionary(p => p.Code, p => p.Name);

    private static object Select(Dictionary<string, string> options) =>
        new { Options = options, Default = "", Null = true, Nulloption = true, Multiple = false, Translate = false };

    private static object Input(int maxLength) => new { Type = "text", Maxlength = maxLength, Null = true, Default = "" };

    private static object OrganizationScreens() => new Dictionary<string, object>
    {
        ["create"] = new Dictionary<string, object> { ["-all-"] = new { Shown = true, Required = false } },
        ["edit"] = new Dictionary<string, object> { ["-all-"] = new { Shown = true, Required = false } },
        ["view"] = new Dictionary<string, object> { ["-all-"] = new { Shown = true } },
    };

    private static object TicketScreens() => new Dictionary<string, object>
    {
        ["create_middle"] = new Dictionary<string, object> { ["ticket.agent"] = new { Shown = true, Required = false } },
        ["edit"] = new Dictionary<string, object> { ["ticket.agent"] = new { Shown = true, Required = false } },
    };
}
