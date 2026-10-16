using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.DbInit;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

// Uso: dotnet run --project tools/Dusiburg.AI.Ticket.DbInit [-- "<connection string>"] [--no-seed] [--allow-non-local]
// Senza connection string usa ConnectionStrings__sql dall'ambiente o, in mancanza, (localdb)\localdev database Ticket.
// La dimensione della colonna VECTOR viene da EMBEDDING_DIMENSIONS (default 768): deve coincidere con il modello di embedding.
const string AllowNonLocal = "--allow-non-local";
const string NoSeed = "--no-seed";

var connectionString = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__sql")
    ?? TicketDatabaseInitializer.LocalConnectionString;

var target = new SqlConnectionStringBuilder(connectionString);

// Il tool cancella il database: fuori da LocalDB serve una conferma esplicita.
if (!target.DataSource.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase) && !args.Contains(AllowNonLocal))
{
    Console.Error.WriteLine($"Il server '{target.DataSource}' non è LocalDB: il database verrebbe cancellato. Aggiungi {AllowNonLocal} per procedere.");
    return 1;
}

int embeddingDimensions;

try
{
    IConfiguration environment = new ConfigurationBuilder()
        .AddInMemoryCollection([new(AiOptions.Keys.EmbeddingDimensions, Environment.GetEnvironmentVariable(AiOptions.Keys.EmbeddingDimensions))])
        .Build();

    embeddingDimensions = AiOptions.ReadEmbeddingDimensions(environment);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var seed = !args.Contains(NoSeed);

Console.WriteLine($"Ricreo il database '{target.InitialCatalog}' su '{target.DataSource}' (VECTOR({embeddingDimensions}))...");

await TicketDatabaseInitializer.RecreateAsync(connectionString, embeddingDimensions, seed);

await using (TicketDbContext db = TicketDatabaseInitializer.Create(connectionString, embeddingDimensions))
{
    await SeedReport.WriteAsync(db, Console.Out);
}

Console.WriteLine();
Console.WriteLine(seed
    ? "Fatto: schemi support, rag e agent creati, lookup popolate dagli enum, catalogo demo inserito. Storico, KB e note di rilascio arrivano con i dati simulati (Fase 3)."
    : "Fatto: schemi support, rag e agent creati, lookup popolate dagli enum, nessun dato demo.");

return 0;
