using System.ComponentModel;
using Dusiburg.AI.Ticket.Mcp.Hosting;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Errors;
using Dusiburg.AI.Ticket.Shared.Knowledge;
using Dusiburg.AI.Ticket.Zammad.Api;
using ModelContextProtocol.Server;

namespace Dusiburg.AI.Ticket.Mcp.Tools;

/// <summary>Note di rilascio di un intervallo di versioni (il risultato strutturato di un tool è un oggetto, non un elenco).</summary>
public sealed record ReleaseNotesResult(string ProductCode, string? FromVersion, string? ToVersion, IReadOnlyList<ReleaseNoteItem> Notes);

/// <summary>Un ticket di Zammad trovato; <see cref="HistoricalKey"/> è il numero dello storico, leggibile per intero con <c>leggi_fonte</c>.</summary>
public sealed record ZammadTicketHit(string Number, string Title, string? State, DateTimeOffset CreatedAt, string? HistoricalKey);

public sealed record ZammadSearchResult(IReadOnlyList<ZammadTicketHit> Tickets);

/// <summary>
/// Tool di <c>ticket-mcp</c> (Fase 9b), tutti in sola lettura. Sono passacarte verso le API di conoscenza dell'Agent e l'API di
/// Zammad: i vincoli (prodotto e versione del cliente, fonti leggibili, numero massimo di chiamate) li impone la guardia della
/// modalità agente, che toglie dallo schema gli argomenti che mette il codice. I testi restituiti sono dati, non istruzioni.
/// </summary>
[McpServerToolType]
public sealed class TicketTools(KnowledgeApiClient knowledge, ZammadClient zammad)
{
    public const int MaxSearchResults = 8;
    public const int MaxCustomerTickets = 10;
    public const int MaxZammadResults = 10;

    [McpServerTool(Name = TicketToolNames.SearchSources, Title = "Cerca fonti",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Cerca nello storico dei ticket risolti, negli articoli della knowledge base e nelle note di rilascio, con la ricerca "
        + "ibrida (vettoriale e per parole). Restituisce al più 8 fonti, dalla più pertinente: chiave (key) da citare, tipo, "
        + "applicabilità (prodotto, modalità, versioni), punteggio, distanza (più bassa = più simile; sotto 0,30 è lo stesso problema) "
        + "e un estratto. Scrivi il testo come lo scriverebbe il cliente: sintomo, codici di errore, contesto.")]
    public async Task<KnowledgeSearchResult> SearchSourcesAsync(
        [Description("Testo da cercare: sintomo, codici di errore, contesto.")] string testo,
        [Description("Codice del prodotto (FF o DA).")] string? prodotto = null,
        [Description("Modalità: OnPrem o Saas.")] DeploymentMode? modalita = null,
        [Description("Versione del cliente, es. 5.2.0.")] string? versione = null,
        CancellationToken cancellationToken = default) =>
        await knowledge.SearchAsync(Required(testo, nameof(testo)), prodotto, modalita, versione, MaxSearchResults, cancellationToken);

    [McpServerTool(Name = TicketToolNames.ReadSource, Title = "Leggi una fonte",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Restituisce il testo completo di una fonte trovata (tutte le sue sezioni): un ticket storico (problema, causa, "
        + "soluzione), un articolo della knowledge base o una nota di rilascio. Chiave assente dall'indice: NOT_FOUND.")]
    public async Task<KnowledgeDocument> ReadSourceAsync(
        [Description("Chiave della fonte, come compare nei risultati: es. HT-2025-0412, KB-FF-0004, RN-FF-5.2.0-03.")] string chiave,
        CancellationToken cancellationToken = default) =>
        await knowledge.GetSourceAsync(Required(chiave, nameof(chiave)), cancellationToken);

    [McpServerTool(Name = TicketToolNames.ReleaseNotes, Title = "Note di rilascio",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Elenca le note di rilascio del prodotto fra due versioni comprese (problemi noti, correzioni, novità), in ordine di "
        + "versione. Serve a capire se un problema è un bug noto di una versione e in quale versione è corretto. Versione "
        + "sconosciuta: VALIDATION_ERROR con l'elenco delle versioni.")]
    public async Task<ReleaseNotesResult> ReleaseNotesAsync(
        [Description("Codice del prodotto (FF o DA).")] string prodotto,
        [Description("Prima versione compresa, es. 5.1.2; vuoto = dalla prima.")] string? versioneDa = null,
        [Description("Ultima versione compresa, es. 5.2.1; vuoto = fino all'ultima.")] string? versioneA = null,
        CancellationToken cancellationToken = default) =>
        new(prodotto, versioneDa, versioneA, await knowledge.ReleaseNotesAsync(Required(prodotto, nameof(prodotto)), versioneDa, versioneA, cancellationToken));

    [McpServerTool(Name = TicketToolNames.CustomerHistory, Title = "Storico del cliente",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Gli ultimi ticket dell'azienda del cliente (al più 10): numero, oggetto, categoria, data e stato. Quelli con "
        + "indexed = true si leggono per intero con leggi_fonte. Utile per capire se il problema si è già presentato.")]
    public async Task<CustomerHistory> CustomerHistoryAsync(
        [Description("Email del referente.")] string email,
        CancellationToken cancellationToken = default) =>
        await knowledge.CustomerTicketsAsync(Required(email, nameof(email)), MaxCustomerTickets, cancellationToken);

    [McpServerTool(Name = TicketToolNames.SearchZammad, Title = "Cerca in Zammad",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Ricerca per parole fra i ticket dell'helpdesk Zammad (al più 10): numero, titolo, stato, data. I ticket importati "
        + "dallo storico hanno historicalKey, la chiave da leggere con leggi_fonte. Meno precisa di cerca_fonti: usala per un codice "
        + "di errore o un termine esatto.")]
    public async Task<ZammadSearchResult> SearchZammadAsync(
        [Description("Parole o codice da cercare, es. FF-E2041.")] string testo,
        [Description("Numero di un ticket da escludere dai risultati.")] string? escludiNumero = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ZammadSearchHit> hits;

        try
        {
            hits = await zammad.SearchTicketsAsync(Required(testo, nameof(testo)), MaxZammadResults + 1, cancellationToken);
        }
        catch (ZammadApiException exception)
        {
            throw new ToolException(ErrorCodes.UpstreamUnavailable, $"Ricerca in Zammad non riuscita ({(int)exception.StatusCode}).");
        }

        return new ZammadSearchResult([.. hits
            .Where(h => h.Number != escludiNumero)
            .Take(MaxZammadResults)
            .Select(h => new ZammadTicketHit(h.Number, h.Title, h.State, h.CreatedAt, string.IsNullOrWhiteSpace(h.RifStorico) ? null : h.RifStorico))]);
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ToolException(ErrorCodes.ValidationError, $"Argomento '{name}' assente.") : value;
}
