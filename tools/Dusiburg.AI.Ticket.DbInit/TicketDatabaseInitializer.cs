using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Seed;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.DbInit;

/// <summary>
/// Crea il database Ticket da zero dal modello EF (G4): nessuna migration, nessuno step intermedio. Le lookup degli enum fanno
/// parte del modello (<c>HasData</c>). Usato dal tool e dalle fixture dei test, con un database dedicato.
/// </summary>
public static class TicketDatabaseInitializer
{
    public const string LocalConnectionString = @"Server=(localdb)\localdev;Database=Ticket;Trusted_Connection=True;TrustServerCertificate=True";

    /// <summary>Connection string di <c>(localdb)\localdev</c> per un altro database (es. quelli dei test).</summary>
    public static string LocalConnectionStringFor(string database) =>
        new SqlConnectionStringBuilder(LocalConnectionString) { InitialCatalog = database }.ConnectionString;

    /// <summary>
    /// Cancella il database se esiste e lo ricrea con i tre schemi, le tabelle e le righe delle lookup; con <paramref name="seed"/>
    /// scrive anche prodotti, versioni e clienti del catalogo demo. <c>rag.EmbeddingInfo</c> resta vuota: la scrive <c>Cli embed</c>.
    /// </summary>
    public static async Task RecreateAsync(
        string connectionString, int embeddingDimensions, bool seed = true, CancellationToken cancellationToken = default)
    {
        await using TicketDbContext db = Create(connectionString, embeddingDimensions);

        await db.Database.EnsureDeletedAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);

        if (seed)
        {
            await DemoCatalogSeeder.SeedAsync(db, cancellationToken);
        }
    }

    /// <summary>Cancella il database se esiste.</summary>
    public static async Task DropAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        // La dimensione non conta per il drop.
        await using TicketDbContext db = Create(connectionString, embeddingDimensions: 1);

        await db.Database.EnsureDeletedAsync(cancellationToken);
    }

    public static TicketDbContext Create(string connectionString, int embeddingDimensions) =>
        new(new DbContextOptionsBuilder<TicketDbContext>().UseSqlServer(connectionString).Options, new TicketDbContextOptions(embeddingDimensions));
}
