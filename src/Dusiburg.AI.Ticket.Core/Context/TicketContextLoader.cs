using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Rag.Cleaning;
using Dusiburg.AI.Ticket.Shared.Demo;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Core.Context;

/// <summary>
/// Passo "contesto" (P4): ticket, articoli pubblici e cliente da Zammad. Il contesto viene dai campi personalizzati
/// dell'organizzazione (scritti da <c>zammad seed</c>); se mancano, da <c>support.Customer</c> per dominio email del referente.
/// Le note interne (anche quelle dell'assistente) non entrano nel thread.
/// </summary>
public sealed class TicketContextLoader(ZammadClient zammad, TicketDbContext db)
{
    /// <summary>Valori dei campi personalizzati scritti da <c>zammad bootstrap</c> (Fase 4).</summary>
    internal const string ModeOnPrem = "onprem";
    internal const string ModeSaas = "saas";

    public async Task<IncomingTicket> LoadAsync(long ticketId, long? triggerArticleId, CancellationToken cancellationToken)
    {
        ZammadTicketDetail ticket = await zammad.GetTicketAsync(ticketId, cancellationToken);
        IReadOnlyList<ZammadArticleDetail> articles = await zammad.GetArticlesAsync(ticketId, cancellationToken);

        ZammadArticleDetail trigger = (triggerArticleId is { } id ? articles.SingleOrDefault(a => a.Id == id) : articles.LastOrDefault(a => a.IsFromCustomer))
            ?? throw new InvalidOperationException($"Ticket {ticket.Number}: articolo del cliente {(triggerArticleId?.ToString() ?? "")} non trovato.");

        CustomerContext customer = await LoadCustomerAsync(ticket, cancellationToken);
        string[] names = [customer.ContactName];

        // Il thread fino all'articolo che ha fatto partire l'elaborazione: le risposte successive non c'erano ancora.
        List<ThreadMessage> thread = [.. articles
            .Where(a => !a.Internal && a.Id <= trigger.Id && a.Sender is "Customer" or "Agent")
            .Select(a => new ThreadMessage(
                a.Id,
                a.Sender == ZammadClient.SenderCustomer ? MessageAuthor.Customer : MessageAuthor.Agent,
                a.CreatedAt,
                a.Sender == ZammadClient.SenderCustomer
                    ? MessageCleaner.Clean(HtmlText.ToPlainText(a.Body, a.ContentType), names)
                    : HtmlText.ToPlainText(a.Body, a.ContentType)))];

        return new IncomingTicket(ticket.Id, ticket.Number, ticket.Title, trigger.Id, thread, customer);
    }

    private async Task<CustomerContext> LoadCustomerAsync(ZammadTicketDetail ticket, CancellationToken cancellationToken)
    {
        ZammadUserRecord? user = ticket.CustomerId is { } customerId ? await zammad.GetUserAsync(customerId, cancellationToken) : null;
        string contactName = string.Join(' ', new[] { user?.Firstname, user?.Lastname }.Where(n => !string.IsNullOrWhiteSpace(n))).Trim();

        if (contactName.Length == 0)
        {
            contactName = user?.Email ?? "cliente";
        }

        ZammadOrganizationDetail? organization = ticket.OrganizationId is { } organizationId
            ? await zammad.GetOrganizationAsync(organizationId, cancellationToken)
            : null;

        if (organization is { Prodotto: { Length: > 0 } product })
        {
            return new CustomerContext(organization.Name, contactName, user?.Email, product, NonEmpty(organization.VersioneProdotto),
                ParseMode(organization.Modalita), ParseSla(organization.LivelloSla), "organizzazione Zammad");
        }

        return await FromDatabaseAsync(user?.Email, contactName, organization?.Name, cancellationToken);
    }

    /// <summary>Fallback per dominio email (P4); senza riscontro il contesto resta ignoto e il retrieval non filtra.</summary>
    private async Task<CustomerContext> FromDatabaseAsync(string? email, string contactName, string? organizationName, CancellationToken cancellationToken)
    {
        string? domain = email?.Split('@').LastOrDefault();
        Customer? customer = domain is null ? null : await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.EmailDomain == domain, cancellationToken);

        if (customer is null)
        {
            return new CustomerContext(organizationName, contactName, email, null, null, null, null, "sconosciuto");
        }

        string product = await db.Products.Where(p => p.Id == customer.ProductId).Select(p => p.Code).SingleAsync(cancellationToken);
        string? version = customer.ProductVersionId is { } versionId
            ? await db.ProductVersions.Where(v => v.Id == versionId).Select(v => v.Version).SingleAsync(cancellationToken)
            : null;

        return new CustomerContext(customer.Name, contactName, email, product, version, customer.DeploymentMode, customer.SlaLevel, "anagrafica clienti");
    }

    internal static DeploymentMode? ParseMode(string? value) => value switch
    {
        ModeOnPrem => DeploymentMode.OnPrem,
        ModeSaas => DeploymentMode.Saas,
        _ => null
    };

    internal static SlaLevel? ParseSla(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out SlaLevel sla) && Enum.IsDefined(sla) ? sla : null;

    /// <summary>Nome del prodotto per il prompt (<c>FF</c> → FattureFlow); codice sconosciuto com'è.</summary>
    public static string ProductName(string? code) =>
        code is null ? "non indicato" : DemoCatalog.Products.SingleOrDefault(p => p.Code == code)?.Name ?? code;

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
