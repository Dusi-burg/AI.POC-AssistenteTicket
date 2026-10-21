using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Cli.Zammad;

internal sealed record ZammadSeedReport(
    int OrganizationsCreated, int OrganizationsPresent, int CustomersCreated, int TicketsCreated, int TicketsPresent, int TitlesUpdated);

/// <summary>
/// <c>zammad seed</c> (fase-4.md §3): organizzazioni, referenti e i ticket storici più recenti dal database <c>Ticket</c>, per il
/// realismo dell'interfaccia. I ticket si creano in <c>import_mode</c> (date originali, nessun trigger: il webhook non parte),
/// che viene sempre rispento. Idempotente sul campo <c>rif_storico</c>; ai ticket già presenti riallinea il titolo all'oggetto
/// del database (Fase 5: gli oggetti con il segnaposto <c>{codice}</c> non espanso, corretti nei dati).
/// </summary>
internal sealed class ZammadSeeder(ZammadClient zammad, TicketDbContext db, IReadOnlyList<string> operators, TextWriter output)
{
    private const string ImportModeSetting = "import_mode";

    public async Task<ZammadSeedReport> RunAsync(int count, CancellationToken cancellationToken)
    {
        Dictionary<string, long> groups = (await zammad.ListAllAsync<ZammadGroup>(ZammadResources.Groups, cancellationToken))
            .Where(g => ZammadDirectory.Groups.Contains(g.Name)).ToDictionary(g => g.Name, g => g.Id);

        if (groups.Count < ZammadDirectory.Groups.Count)
        {
            throw new InvalidOperationException("Gruppi del POC assenti in Zammad: lanciare prima 'zammad bootstrap'.");
        }

        (int organizationsCreated, int organizationsPresent) = await SeedOrganizationsAsync(cancellationToken);
        ZammadUsers users = await ZammadUsers.LoadAsync(zammad, cancellationToken);

        List<HistoricalTicket> tickets = await db.HistoricalTickets
            .Where(t => !t.IsTestSet)
            .OrderByDescending(t => t.OpenedAt).ThenByDescending(t => t.Id)
            .Take(count)
            .Include(t => t.Messages)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        Dictionary<string, ZammadTicket> present = (await zammad.ListAllAsync<ZammadTicket>(ZammadResources.Tickets, cancellationToken))
            .Where(t => !string.IsNullOrEmpty(t.RifStorico)).ToDictionary(t => t.RifStorico!);

        int customersBefore = users.CustomersCreated;
        int created = await ImportTicketsAsync([.. tickets.Where(t => !present.ContainsKey(t.Number))], groups, users, cancellationToken);
        int titlesUpdated = 0;

        // Anche i ticket già presenti ritrovano il loro id (dopo un DbInit il database riparte senza) e il titolo dell'oggetto.
        foreach (HistoricalTicket ticket in tickets.Where(t => present.ContainsKey(t.Number)))
        {
            ZammadTicket existing = present[ticket.Number];
            ticket.ZammadTicketId = (int)existing.Id;

            if (existing.Title != ticket.Subject)
            {
                await zammad.UpdateAsync<ZammadTicket>(ZammadResources.Tickets, existing.Id, new Dictionary<string, object?> { ["title"] = ticket.Subject }, cancellationToken);
                output.WriteLine($"  ~ {ticket.Number} → titolo del ticket {existing.Id} riallineato");
                titlesUpdated++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return new ZammadSeedReport(
            organizationsCreated, organizationsPresent, users.CustomersCreated - customersBefore, created, tickets.Count - created, titlesUpdated);
    }

    private async Task<(int Created, int Present)> SeedOrganizationsAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, ZammadOrganization> existing = (await zammad.ListAllAsync<ZammadOrganization>(ZammadResources.Organizations, cancellationToken))
            .ToDictionary(o => o.Name, StringComparer.Ordinal);
        Lookups lookups = await Lookups.LoadAsync(db, cancellationToken);
        List<Customer> customers = await db.Customers.OrderBy(c => c.Id).ToListAsync(cancellationToken);
        int created = 0;

        foreach (Customer customer in customers)
        {
            if (!existing.TryGetValue(customer.Name, out ZammadOrganization? organization))
            {
                organization = await zammad.CreateAsync<ZammadOrganization>(ZammadResources.Organizations, new Dictionary<string, object?>
                {
                    ["name"] = customer.Name,
                    ["domain"] = customer.EmailDomain,
                    // P4: i clienti nuovi con l'email del dominio entrano da soli nell'organizzazione.
                    ["domain_assignment"] = true,
                    [ZammadDirectory.ProductField] = lookups.Products[customer.ProductId],
                    [ZammadDirectory.VersionField] = lookups.VersionOf(customer.ProductVersionId) ?? string.Empty,
                    [ZammadDirectory.ModeField] = ZammadDirectory.ModeValue(customer.DeploymentMode),
                    [ZammadDirectory.SlaField] = ZammadDirectory.SlaValue(customer.SlaLevel),
                    ["note"] = "Cliente fittizio del POC Assistente ticket"
                }, cancellationToken);
                created++;
            }

            customer.ZammadOrganizationId = (int)organization.Id;
        }

        await db.SaveChangesAsync(cancellationToken);
        output.WriteLine($"  organizzazioni: {created} create, {customers.Count - created} già presenti");

        return (created, customers.Count - created);
    }

    /// <summary>Ticket e articoli con le date originali in <c>import_mode</c>, sempre rispento alla fine (anche dopo un errore).</summary>
    private async Task<int> ImportTicketsAsync(
        List<HistoricalTicket> tickets, Dictionary<string, long> groups, ZammadUsers users, CancellationToken cancellationToken)
    {
        if (tickets.Count == 0)
        {
            return 0;
        }

        Dictionary<int, (string Product, string? Version, string Domain)> context = await TicketContextAsync(tickets, cancellationToken);
        ZammadSetting importMode = (await zammad.GetAsync<List<ZammadSetting>>(ZammadResources.Settings, cancellationToken)).Single(s => s.Name == ImportModeSetting);

        await zammad.SetSettingAsync(importMode.Id, true, cancellationToken);

        try
        {
            foreach (HistoricalTicket ticket in tickets.OrderBy(t => t.OpenedAt))
            {
                (string product, string? version, string domain) = context[ticket.Id];
                long customerId = await users.EnsureCustomerAsync(ticket.ContactName, ticket.ContactEmail, domain, cancellationToken);

                ticket.ZammadTicketId = (int)await ImportTicketAsync(ticket, product, version, groups[ZammadDirectory.GroupFor(product)], customerId, users, cancellationToken);
                output.WriteLine($"  + {ticket.Number} → ticket {ticket.ZammadTicketId}");
            }
        }
        finally
        {
            await zammad.SetSettingAsync(importMode.Id, false, CancellationToken.None);
        }

        return tickets.Count;
    }

    private async Task<long> ImportTicketAsync(
        HistoricalTicket ticket, string product, string? version, long groupId, long customerId, ZammadUsers users, CancellationToken cancellationToken)
    {
        List<TicketMessage> messages = [.. ticket.Messages.OrderBy(m => m.Sequence)];
        string? owner = messages.Where(m => m.Author == MessageAuthor.Agent).Select(m => OperatorOf(m.Body)).FirstOrDefault();

        // Zammad scrive come autore l'utente della richiesta (created_by_id passato viene ignorato): ticket e articoli si creano
        // per conto di chi li ha scritti, così l'interfaccia mostra cliente e operatori veri.
        ZammadTicket created = await zammad.CreateAsync<ZammadTicket>(ZammadResources.Tickets, new Dictionary<string, object?>
        {
            ["title"] = ticket.Subject,
            ["group_id"] = groupId,
            ["created_at"] = ticket.OpenedAt,
            ["article"] = Article(ticket, messages[0], ticketId: null)
        }, cancellationToken, onBehalfOf: ticket.ContactEmail);

        foreach (TicketMessage message in messages.Skip(1))
        {
            await zammad.CreateAsync<ZammadArticle>(ZammadResources.Articles, Article(ticket, message, created.Id), cancellationToken, onBehalfOf: AuthorOf(ticket, message));
        }

        // Stato finale, owner, priorità, chiusura e campi del POC: li imposta l'amministratore, che il cliente non può toccare.
        await zammad.UpdateAsync<ZammadTicket>(ZammadResources.Tickets, created.Id, new Dictionary<string, object?>
        {
            ["customer_id"] = customerId,
            ["owner_id"] = owner is null ? null : users.OperatorId(owner),
            ["state"] = StateOf(ticket, messages),
            ["priority_id"] = (int)ticket.Priority,
            ["close_at"] = ticket.ClosedAt,
            [ZammadDirectory.ProductField] = product,
            [ZammadDirectory.VersionField] = version ?? string.Empty,
            [ZammadDirectory.HistoricalNumberField] = ticket.Number
        }, cancellationToken);

        return created.Id;
    }

    /// <summary>Articolo del cliente (dal portale) o dell'operatore (risposta pubblica), con data originale.</summary>
    private Dictionary<string, object?> Article(HistoricalTicket ticket, TicketMessage message, long? ticketId)
    {
        bool fromCustomer = message.Author == MessageAuthor.Customer;

        var article = new Dictionary<string, object?>
        {
            ["subject"] = ticket.Subject,
            ["body"] = message.Body,
            ["content_type"] = "text/plain",
            ["type"] = fromCustomer ? "web" : "note",
            ["sender"] = fromCustomer ? "Customer" : "Agent",
            ["internal"] = false,
            // In import_mode Zammad non compila il mittente (fase-0.md V6).
            ["from"] = fromCustomer ? $"{ticket.ContactName} <{ticket.ContactEmail}>" : OperatorOf(message.Body),
            ["created_at"] = message.SentAt
        };

        if (ticketId is not null)
        {
            article["ticket_id"] = ticketId;
        }

        return article;
    }

    private string AuthorOf(HistoricalTicket ticket, TicketMessage message) =>
        message.Author == MessageAuthor.Customer ? ticket.ContactEmail : ZammadDirectory.OperatorEmail(OperatorOf(message.Body));

    /// <summary>Il generatore firma le risposte con il nome dell'operatore: è il modo di ritrovarlo.</summary>
    private string OperatorOf(string body) =>
        operators.FirstOrDefault(name => body.Contains(name, StringComparison.Ordinal)) ?? operators[0];

    private static string StateOf(HistoricalTicket ticket, List<TicketMessage> messages) => ticket.State switch
    {
        TicketState.Open => messages.Count == 1 ? "new" : "open",
        _ => "closed"
    };

    private async Task<Dictionary<int, (string Product, string? Version, string Domain)>> TicketContextAsync(List<HistoricalTicket> tickets, CancellationToken cancellationToken)
    {
        Lookups lookups = await Lookups.LoadAsync(db, cancellationToken);
        Dictionary<int, string> domains = await db.Customers.ToDictionaryAsync(c => c.Id, c => c.EmailDomain, cancellationToken);

        return tickets.ToDictionary(t => t.Id, t => (lookups.Products[t.ProductId], lookups.VersionOf(t.ProductVersionId), domains[t.CustomerId]));
    }

    /// <summary>Codici di prodotto e versioni per id: tabelle piccole, lette una volta.</summary>
    private sealed record Lookups(Dictionary<short, string> Products, Dictionary<int, string> Versions)
    {
        public static async Task<Lookups> LoadAsync(TicketDbContext db, CancellationToken cancellationToken) => new(
            await db.Products.ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken),
            await db.ProductVersions.ToDictionaryAsync(v => v.Id, v => v.Version, cancellationToken));

        public string? VersionOf(int? versionId) => versionId is null ? null : Versions[versionId.Value];
    }
}
