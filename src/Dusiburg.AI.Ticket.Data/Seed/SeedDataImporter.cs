using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Shared.Demo;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Data.Seed;

/// <summary>
/// Carica in <c>support</c> il contenuto di <c>data/</c> su un database in cui <see cref="DemoCatalogSeeder"/> ha già scritto
/// prodotti, versioni e clienti demo: gli altri clienti, la knowledge base, le note di rilascio e i ticket generati. L'ordine
/// di inserimento segue i file, così gli Id sono ripetibili.
/// </summary>
public static class SeedDataImporter
{
    private const int TicketBatchSize = 200;

    public static async Task ImportAsync(TicketDbContext db, SeedData data, CancellationToken cancellationToken = default)
    {
        Dictionary<(string Product, string Version), int> versions = await db.ProductVersions
            .Select(v => new { Product = v.Product.Code, v.Version, v.Id })
            .ToDictionaryAsync(v => (v.Product, v.Version), v => v.Id, cancellationToken);
        Dictionary<string, short> products = await db.Products.ToDictionaryAsync(p => p.Code, p => p.Id, cancellationToken);

        await ImportCustomersAsync(db, data, products, versions, cancellationToken);
        await ImportKnowledgeAsync(db, data, products, versions, cancellationToken);

        if (data.Tickets is not null)
        {
            await ImportTicketsAsync(db, data.Tickets, products, versions, cancellationToken);
        }
    }

    private static async Task ImportCustomersAsync(
        TicketDbContext db, SeedData data, Dictionary<string, short> products, Dictionary<(string, string), int> versions,
        CancellationToken cancellationToken)
    {
        HashSet<string> demo = [.. DemoCatalog.Customers.Select(c => c.EmailDomain)];

        db.Customers.AddRange(data.Customers.Where(c => !demo.Contains(c.EmailDomain)).Select(c => new Customer
        {
            EmailDomain = c.EmailDomain,
            Name = c.Name,
            ProductId = products[c.Product],
            ProductVersionId = c.Version is null ? null : versions[(c.Product, c.Version)],
            DeploymentMode = c.Mode,
            SlaLevel = c.Sla
        }));

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task ImportKnowledgeAsync(
        TicketDbContext db, SeedData data, Dictionary<string, short> products, Dictionary<(string, string), int> versions,
        CancellationToken cancellationToken)
    {
        db.KbArticles.AddRange(data.KbArticles.Select(a =>
        {
            string product = a.Products.Single();

            return new KbArticle
            {
                Code = a.Code,
                ProductId = products[product],
                DeploymentMode = a.Mode,
                VersionFromId = a.VersionFrom is null ? null : versions[(product, a.VersionFrom)],
                VersionToId = a.VersionTo is null ? null : versions[(product, a.VersionTo)],
                Title = a.Title,
                Body = KbBody(a),
                UpdatedOn = a.UpdatedOn
            };
        }));

        db.ReleaseNotes.AddRange(data.ReleaseNotes.Select(n => new ReleaseNote
        {
            Code = n.Code,
            ProductVersionId = versions[(n.Product, n.Version)],
            Type = n.Type,
            Title = n.Title,
            Body = n.Body
        }));

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Corpo dell'articolo con le sezioni come titoli Markdown: il chunking della Fase 5 divide su questi.</summary>
    public static string KbBody(KbArticleDefinition article) =>
        string.Join("\n\n", article.Sections.Select(s => $"## {s.Title}\n\n{s.Body}"));

    private static async Task ImportTicketsAsync(
        TicketDbContext db, TicketsFile tickets, Dictionary<string, short> products, Dictionary<(string, string), int> versions,
        CancellationToken cancellationToken)
    {
        Dictionary<string, int> customers = await db.Customers.ToDictionaryAsync(c => c.EmailDomain, c => c.Id, cancellationToken);
        Dictionary<string, HistoricalTicket> byNumber = [];

        foreach (GeneratedTicket[] batch in tickets.Tickets.Chunk(TicketBatchSize))
        {
            foreach (GeneratedTicket ticket in batch)
            {
                HistoricalTicket entity = ToEntity(ticket, customers, products, versions);

                byNumber[ticket.Number] = entity;
                db.HistoricalTickets.Add(entity);
            }

            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        // I duplicati puntano a ticket precedenti: gli Id esistono solo dopo il salvataggio.
        foreach (GeneratedTicket duplicate in tickets.Tickets.Where(t => t.DuplicateOf is not null))
        {
            HistoricalTicket entity = byNumber[duplicate.Number];
            entity.DuplicateOfId = byNumber[duplicate.DuplicateOf!].Id;
            db.HistoricalTickets.Attach(entity).Property(t => t.DuplicateOfId).IsModified = true;
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    private static HistoricalTicket ToEntity(
        GeneratedTicket ticket, Dictionary<string, int> customers, Dictionary<string, short> products, Dictionary<(string, string), int> versions)
    {
        var entity = new HistoricalTicket
        {
            Number = ticket.Number,
            CustomerId = customers[ticket.Customer],
            ProductId = products[ticket.Product],
            ProductVersionId = ticket.Version is null ? null : versions[(ticket.Product, ticket.Version)],
            DeploymentMode = ticket.Mode,
            Category = ticket.Category,
            Priority = ticket.Priority,
            State = ticket.State,
            Subject = ticket.Subject,
            Description = ticket.Description,
            OpenedAt = ticket.OpenedAt,
            ClosedAt = ticket.ClosedAt,
            IsTestSet = ticket.IsTestSet,
            CaseCode = ticket.CaseCode,
            TrapKind = ticket.TrapKind,
            Resolution = ticket.Resolution is null
                ? null
                : new Resolution { Cause = ticket.Resolution.Cause, Solution = ticket.Resolution.Solution, IsVerified = ticket.Resolution.IsVerified }
        };

        entity.Messages.AddRange(ticket.Messages.Select((m, index) => new TicketMessage
        {
            Sequence = (short)(index + 1), Author = m.Author, Body = m.Body, SentAt = m.SentAt
        }));

        return entity;
    }
}
