using System.Globalization;
using System.Text.Json;

namespace Dusiburg.AI.Ticket.Zammad.Api;

/// <summary>
/// Operazioni dell'Agent (Fase 6): lettura di ticket, articoli, cliente e organizzazione; bozza condivisa, nota interna e tag;
/// ricerca per il polling. Tutte con il token di <c>ai-assistant</c>.
/// </summary>
public sealed partial class ZammadClient
{
    public const string SenderCustomer = "Customer";
    public const string SenderAgent = "Agent";

    /// <summary>Nome dello stato chiuso (fra quelli predefiniti di Zammad).</summary>
    public const string StateClosed = "closed";

    /// <summary>Con <c>expand=true</c>, così lo stato arriva per nome (<see cref="ZammadTicketDetail.State"/>).</summary>
    public async Task<ZammadTicketDetail> GetTicketAsync(long ticketId, CancellationToken cancellationToken) =>
        await GetAsync<ZammadTicketDetail>($"{ZammadResources.Tickets}/{ticketId}?expand=true", cancellationToken);

    /// <summary>Id del ticket con questo numero (quello mostrato in Zammad, es. 71164); <c>null</c> se non esiste.</summary>
    public async Task<long?> FindTicketIdByNumberAsync(string number, CancellationToken cancellationToken)
    {
        List<ZammadTicketDetail> found = await GetAsync<List<ZammadTicketDetail>>(
            $"{ZammadResources.Tickets}/search?query={Uri.EscapeDataString($"number:{number}")}&limit=1&expand=true", cancellationToken);

        return found.FirstOrDefault(t => t.Number == number)?.Id;
    }

    /// <summary>Articoli del ticket in ordine di creazione, con mittente e tipo per nome.</summary>
    public async Task<IReadOnlyList<ZammadArticleDetail>> GetArticlesAsync(long ticketId, CancellationToken cancellationToken) =>
        [.. (await GetAsync<List<ZammadArticleDetail>>($"{ZammadResources.Articles}/by_ticket/{ticketId}", cancellationToken)).OrderBy(a => a.Id)];

    public async Task<ZammadUserRecord> GetUserAsync(long userId, CancellationToken cancellationToken) =>
        await GetAsync<ZammadUserRecord>($"{ZammadResources.Users}/{userId}", cancellationToken);

    /// <summary>Organizzazione con i campi personalizzati del POC (P4).</summary>
    public async Task<ZammadOrganizationDetail> GetOrganizationAsync(long organizationId, CancellationToken cancellationToken) =>
        await GetAsync<ZammadOrganizationDetail>($"{ZammadResources.Organizations}/{organizationId}", cancellationToken);

    /// <summary>
    /// Crea o sovrascrive la bozza condivisa del ticket (una sola per ticket, fase-0.md §4 ter): <paramref name="newArticle"/> sono i
    /// campi del form di risposta, che l'editor carica così come sono. È un <c>PUT</c>: ripeterlo non duplica nulla.
    /// </summary>
    public async Task SaveSharedDraftAsync(long ticketId, IReadOnlyDictionary<string, object?> newArticle, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Put, $"{ZammadResources.Tickets}/{ticketId}/shared_draft",
            new { NewArticle = newArticle, TicketAttributes = new { }, FormId = Guid.NewGuid().ToString() }, onBehalfOf: null, cancellationToken);

    /// <summary>
    /// HTML della bozza condivisa del ticket, <c>null</c> se non c'è (all'invio Zammad la cancella). La risposta mette la bozza in
    /// <c>assets.TicketSharedDraftZoom.&lt;id&gt;.new_article</c> (fase-0.md §4 ter).
    /// </summary>
    public async Task<string?> GetSharedDraftBodyAsync(long ticketId, CancellationToken cancellationToken)
    {
        JsonElement response;

        try
        {
            response = await GetAsync<JsonElement>($"{ZammadResources.Tickets}/{ticketId}/shared_draft", cancellationToken);
        }
        catch (ZammadApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.TryGetProperty("assets", out JsonElement assets) || !assets.TryGetProperty("TicketSharedDraftZoom", out JsonElement drafts))
        {
            return null;
        }

        return drafts.EnumerateObject()
            .Select(d => d.Value.TryGetProperty("new_article", out JsonElement article) && article.TryGetProperty("body", out JsonElement body) ? body.GetString() : null)
            .FirstOrDefault(b => b is not null);
    }

    public async Task DeleteSharedDraftAsync(long ticketId, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Delete, $"{ZammadResources.Tickets}/{ticketId}/shared_draft", body: null, onBehalfOf: null, cancellationToken);

    /// <summary>Aggiunge un tag al ticket; un tag già presente non dà errore.</summary>
    public async Task AddTicketTagAsync(long ticketId, string tag, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Post, "tags/add", new { Item = tag, Object = "Ticket", OId = ticketId }, onBehalfOf: null, cancellationToken);

    /// <summary>
    /// Ticket aggiornati da <paramref name="since"/> in poi, dal più vecchio (V9). La ricerca passa da Elasticsearch: un articolo
    /// nuovo è visibile dopo qualche secondo, quindi chi fa polling arretra il cursore e scarta i doppioni.
    /// </summary>
    public async Task<IReadOnlyList<ZammadTicketDetail>> SearchTicketsUpdatedSinceAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken)
    {
        string from = since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        string query = Uri.EscapeDataString($"updated_at:[{from} TO *]");

        return await GetAsync<List<ZammadTicketDetail>>(
            $"{ZammadResources.Tickets}/search?query={query}&sort_by=updated_at&order_by=asc&limit={limit}&expand=true", cancellationToken);
    }
}

public sealed partial class ZammadClient
{
    /// <summary>Ricerca libera (Elasticsearch) fra i ticket visibili al token, dal più pertinente (Fase 9b, tool <c>cerca_ticket_zammad</c>).</summary>
    public async Task<IReadOnlyList<ZammadSearchHit>> SearchTicketsAsync(string text, int limit, CancellationToken cancellationToken) =>
        await GetAsync<List<ZammadSearchHit>>(
            $"{ZammadResources.Tickets}/search?query={Uri.EscapeDataString(text)}&limit={limit}&expand=true", cancellationToken);
}

/// <summary>Ticket trovato dalla ricerca: <see cref="RifStorico"/> è il numero del ticket storico importato (Fase 4), se lo è.</summary>
public sealed record ZammadSearchHit(long Id, string Number, string Title, string? State, string? RifStorico, DateTimeOffset CreatedAt);

/// <param name="State">Nome dello stato (<c>new</c>, <c>open</c>, <c>closed</c>…), presente solo con <c>expand=true</c>.</param>
public sealed record ZammadTicketDetail(
    long Id, string Number, string Title, long GroupId, long? CustomerId, long? OrganizationId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? State = null)
{
    public bool IsClosed => string.Equals(State, ZammadClient.StateClosed, StringComparison.Ordinal);
}

/// <summary>Articolo con il testo: <see cref="Sender"/> è <c>Customer</c>, <c>Agent</c> o <c>System</c>; <see cref="Type"/> per esempio <c>web</c>, <c>email</c>, <c>note</c>.</summary>
public sealed record ZammadArticleDetail(
    long Id,
    long TicketId,
    string? From,
    string? Sender,
    string? Type,
    bool Internal,
    string? Subject,
    string Body,
    string? ContentType,
    long? CreatedById,
    DateTimeOffset CreatedAt)
{
    public bool IsFromCustomer => !Internal && string.Equals(Sender, ZammadClient.SenderCustomer, StringComparison.Ordinal);

    /// <summary>Risposta pubblica di un operatore: le note dell'assistente sono interne e restano fuori.</summary>
    public bool IsAgentReply => !Internal && string.Equals(Sender, ZammadClient.SenderAgent, StringComparison.Ordinal);
}

/// <summary>Organizzazione con i campi personalizzati creati da <c>zammad bootstrap</c> (prodotto, versione, modalità, SLA).</summary>
public sealed record ZammadOrganizationDetail(
    long Id, string Name, string? Domain, string? Prodotto, string? VersioneProdotto, string? Modalita, string? LivelloSla);
