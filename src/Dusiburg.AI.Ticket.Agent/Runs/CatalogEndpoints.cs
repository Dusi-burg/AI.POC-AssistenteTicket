using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Shared.Catalog;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Agent.Runs;

/// <summary>
/// <c>/api/catalog</c> (Fase 7): i dati demo del "gestionale" simulato per la pagina "Dati demo" della console, in sola lettura.
/// Pochi record (2 prodotti, ~40 clienti, ~50 articoli, ~60 note): una sola chiamata.
/// </summary>
public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/catalog", GetAsync);

        return group;
    }

    internal static async Task<Ok<CatalogView>> GetAsync(TicketDbContext db, CancellationToken cancellationToken)
    {
        List<Product> products = await db.Products.AsNoTracking().Include(p => p.Versions).OrderBy(p => p.Code).ToListAsync(cancellationToken);
        Dictionary<short, string> productCodes = products.ToDictionary(p => p.Id, p => p.Code);
        Dictionary<int, string> versions = products.SelectMany(p => p.Versions).ToDictionary(v => v.Id, v => v.Version);

        List<Customer> customers = await db.Customers.AsNoTracking().OrderBy(c => c.Name).ToListAsync(cancellationToken);
        List<KbArticle> articles = await db.KbArticles.AsNoTracking().OrderBy(a => a.Code).ToListAsync(cancellationToken);
        List<ReleaseNote> notes = await db.ReleaseNotes.AsNoTracking().OrderBy(n => n.Code).ToListAsync(cancellationToken);
        Dictionary<int, short> productOfVersion = products.SelectMany(p => p.Versions).ToDictionary(v => v.Id, v => v.ProductId);

        return TypedResults.Ok(new CatalogView(
            [.. products.Select(p => new ProductView(p.Code, p.Name,
                [.. p.Versions.OrderBy(v => v.Sequence).Select(v => new VersionView(v.Version, v.ReleasedOn, v.EndOfSupportOn))]))],
            [.. customers.Select(c => new CustomerView(c.Name, c.EmailDomain, productCodes[c.ProductId],
                c.ProductVersionId is { } v ? versions[v] : null, c.DeploymentMode, c.SlaLevel, c.ZammadOrganizationId is not null))],
            [.. articles.Select(a => new KbArticleView(a.Code, productCodes[a.ProductId], a.DeploymentMode,
                a.VersionFromId is { } from ? versions[from] : null, a.VersionToId is { } to ? versions[to] : null, a.Title, a.Body, a.UpdatedOn))],
            [.. notes.Select(n => new ReleaseNoteView(n.Code, productCodes[productOfVersion[n.ProductVersionId]], versions[n.ProductVersionId], n.Type, n.Title, n.Body))]));
    }
}
