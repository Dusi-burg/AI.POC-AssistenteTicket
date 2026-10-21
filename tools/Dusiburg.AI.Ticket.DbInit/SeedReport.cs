using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.DbInit;

/// <summary>Riepilogo finale letto dal database: i volumi del PLAN §6.1 e la dimensione della colonna <c>VECTOR</c>.</summary>
public static class SeedReport
{
    public static async Task WriteAsync(TicketDbContext db, TextWriter output, CancellationToken cancellationToken = default)
    {
        int products = await db.Products.CountAsync(cancellationToken);
        int versions = await db.ProductVersions.CountAsync(cancellationToken);
        int customers = await db.Customers.CountAsync(cancellationToken);
        int onPrem = await db.Customers.CountAsync(c => c.DeploymentMode == DeploymentMode.OnPrem, cancellationToken);
        int articles = await db.KbArticles.CountAsync(cancellationToken);
        int notes = await db.ReleaseNotes.CountAsync(cancellationToken);
        int messages = await db.TicketMessages.CountAsync(cancellationToken);
        int chunks = await db.Chunks.CountAsync(cancellationToken);

        output.WriteLine();
        output.WriteLine($"support: prodotti {products} · versioni {versions} · clienti {customers} (on-premise {onPrem}, SaaS {customers - onPrem})");
        output.WriteLine($"         articoli KB {articles} · note di rilascio {notes} · messaggi {messages}");

        await WriteTicketsAsync(db, output, cancellationToken);

        output.WriteLine();
        output.WriteLine($"rag:     chunk {chunks} · colonna Embedding VECTOR({db.EmbeddingDimensions})");
    }

    private static async Task WriteTicketsAsync(TicketDbContext db, TextWriter output, CancellationToken cancellationToken)
    {
        var indexed = await db.HistoricalTickets
            .Where(t => !t.IsTestSet)
            .GroupBy(t => new { t.State, HasResolution = t.Resolution != null, IsDuplicate = t.DuplicateOfId != null })
            .Select(g => new { g.Key.State, g.Key.HasResolution, g.Key.IsDuplicate, Count = g.Count() })
            .ToListAsync(cancellationToken);
        int testSet = await db.HistoricalTickets.CountAsync(t => t.IsTestSet, cancellationToken);
        int traps = await db.HistoricalTickets.CountAsync(t => t.TrapKind != null, cancellationToken);
        int cases = await db.HistoricalTickets.Where(t => t.CaseCode != null).Select(t => t.CaseCode).Distinct().CountAsync(cancellationToken);

        int total = indexed.Sum(g => g.Count);
        int withSolution = indexed.Where(g => g.HasResolution).Sum(g => g.Count);

        output.WriteLine();
        output.WriteLine($"Ticket storici indicizzabili {total}: risolti con soluzione {withSolution} ({(double)withSolution / Math.Max(total, 1):P0}), " +
            $"senza soluzione {indexed.Where(g => g.State == TicketState.Unresolved).Sum(g => g.Count)}, " +
            $"aperti {indexed.Where(g => g.State == TicketState.Open).Sum(g => g.Count)}, duplicati {indexed.Where(g => g.IsDuplicate).Sum(g => g.Count)}");
        output.WriteLine($"Test set {testSet} (trabocchetti {traps}), esclusi dall'indice · casi della matrice {cases}");
    }
}
