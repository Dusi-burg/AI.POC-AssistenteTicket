using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Shared.Demo;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Data.Seed;

/// <summary>
/// Scrive in <c>support</c> prodotti, versioni e clienti di <see cref="DemoCatalog"/> su un database appena creato. Storico,
/// knowledge base e note di rilascio arrivano dai file di <c>data/</c> (Fase 3).
/// </summary>
public static class DemoCatalogSeeder
{
    public static async Task SeedAsync(TicketDbContext db, CancellationToken cancellationToken = default)
    {
        List<Product> products = DemoCatalog.Products.Select(ToProduct).ToList();

        db.Products.AddRange(products);
        await db.SaveChangesAsync(cancellationToken);

        db.Customers.AddRange(DemoCatalog.Customers.Select(customer => ToCustomer(customer, products)));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Product ToProduct(DemoProduct demo)
    {
        var product = new Product { Code = demo.Code, Name = demo.Name };

        product.Versions.AddRange(demo.Versions.Select(version => new ProductVersion
        {
            Version = version.Version,
            Sequence = demo.SequenceOf(version.Version),
            ReleasedOn = version.ReleasedOn,
            EndOfSupportOn = version.EndOfSupportOn
        }));

        return product;
    }

    private static Customer ToCustomer(DemoCustomer demo, IReadOnlyList<Product> products)
    {
        Product product = products.Single(p => p.Code == demo.ProductCode);

        return new Customer
        {
            EmailDomain = demo.EmailDomain,
            Name = demo.Name,
            ProductId = product.Id,
            ProductVersionId = demo.Version is null ? null : product.Versions.Single(v => v.Version == demo.Version).Id,
            DeploymentMode = demo.DeploymentMode,
            SlaLevel = demo.SlaLevel
        };
    }
}
