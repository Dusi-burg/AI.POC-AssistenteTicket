using Dusiburg.AI.Ticket.Data.Seed;
using Dusiburg.AI.Ticket.DataGen;
using Dusiburg.AI.Ticket.Shared.Domain;

// Uso: dotnet run --project tools/Dusiburg.AI.Ticket.DataGen [-- <cartella data>] [--check]
// Rigenera data/tickets.json dai testi di data/ con il seed fisso. Con --check non scrive: verifica che il file sia aggiornato.
const string Check = "--check";

string? dataDirectory = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
    ?? SeedData.FindDataDirectory(Directory.GetCurrentDirectory());

if (dataDirectory is null)
{
    Console.Error.WriteLine("Cartella data/ non trovata: lanciare dalla root del repository o indicarla come argomento.");
    return 1;
}

SeedData data = SeedData.Load(dataDirectory);
TicketsFile tickets = new TicketGenerator(data).Generate();
string json = TicketsJson.Serialize(tickets);
string path = Path.Combine(dataDirectory, SeedData.TicketsFileName);

if (args.Contains(Check))
{
    bool upToDate = File.Exists(path) && TicketsJson.Normalize(File.ReadAllText(path)) == TicketsJson.Normalize(json);
    Console.WriteLine(upToDate ? $"{path} è aggiornato." : $"{path} non corrisponde ai testi di data/: rigenerarlo.");

    return upToDate ? 0 : 1;
}

File.WriteAllText(path, json);

IReadOnlyList<GeneratedTicket> all = tickets.Tickets;
List<GeneratedTicket> indexed = [.. all.Where(t => !t.IsTestSet)];

Console.WriteLine($"Scritto {path}: {all.Count} ticket (seed {tickets.RandomSeed}).");
Console.WriteLine($"Indicizzabili {indexed.Count}: risolti con soluzione {indexed.Count(t => t.Resolution is not null)}, " +
    $"senza soluzione {indexed.Count(t => t.State == TicketState.Unresolved)}, aperti {indexed.Count(t => t.State == TicketState.Open)}, " +
    $"duplicati {indexed.Count(t => t.DuplicateOf is not null)}");
Console.WriteLine($"Test set {all.Count - indexed.Count}: dai casi {all.Count(t => t.IsTestSet && t.TrapKind is null)}, trabocchetti {all.Count(t => t.TrapKind is not null)}");
Console.WriteLine($"Casi {data.Cases.Select(c => c.Code).Distinct().Count()} ({data.Cases.Count} per prodotto), clienti {data.Customers.Count}, " +
    $"articoli KB {data.KbArticles.Count}, note di rilascio {data.ReleaseNotes.Count}");

return 0;
