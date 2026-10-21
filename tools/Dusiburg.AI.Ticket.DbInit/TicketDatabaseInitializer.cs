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
    /// Cancella il database se esiste e lo ricrea con i tre schemi, le tabelle e le righe delle lookup. Con <paramref name="seed"/>
    /// scrive il catalogo demo e il contenuto di <c>data/</c> (clienti, KB, note di rilascio, ticket generati), cercando la cartella
    /// da quella dell'eseguibile in su. <c>rag</c> e <c>agent</c> restano vuoti: li riempiono <c>Cli embed</c> e l'Agent.
    /// </summary>
    /// <returns>I dati caricati, <c>null</c> senza seed.</returns>
    public static async Task<SeedData?> RecreateAsync(
        string connectionString, int embeddingDimensions, bool seed = true, CancellationToken cancellationToken = default)
    {
        SeedData? data = seed ? LoadSeedData() : null;

        await using TicketDbContext db = Create(connectionString, embeddingDimensions);

        await db.Database.EnsureDeletedAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);

        if (data is not null)
        {
            await DemoCatalogSeeder.SeedAsync(db, cancellationToken);
            await SeedDataImporter.ImportAsync(db, data, cancellationToken);
        }

        return data;
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

    /// <summary><c>data/</c> del repository, dalla cartella corrente o da quella dell'eseguibile in su.</summary>
    public static SeedData LoadSeedData()
    {
        string directory = SeedData.FindDataDirectory(Directory.GetCurrentDirectory())
            ?? SeedData.FindDataDirectory(AppContext.BaseDirectory)
            ?? throw new DirectoryNotFoundException("Cartella data/ del repository non trovata: lanciare DbInit dalla root del repository.");

        SeedData data = SeedData.Load(directory);

        return data.Tickets is not null
            ? data
            : throw new FileNotFoundException($"{SeedData.TicketsFileName} mancante in {directory}: generarlo con tools/Dusiburg.AI.Ticket.DataGen.");
    }
}
