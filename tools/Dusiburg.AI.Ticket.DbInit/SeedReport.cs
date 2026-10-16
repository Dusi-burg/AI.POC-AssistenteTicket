using Dusiburg.AI.Ticket.Data;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.DbInit;

/// <summary>Riepilogo finale: righe per tabella lette dal database e dimensione della colonna <c>VECTOR</c>.</summary>
public static class SeedReport
{
    public static async Task WriteAsync(TicketDbContext db, TextWriter output, CancellationToken cancellationToken = default)
    {
        int products = await db.Products.CountAsync(cancellationToken);
        int versions = await db.ProductVersions.CountAsync(cancellationToken);
        int customers = await db.Customers.CountAsync(cancellationToken);
        int tickets = await db.HistoricalTickets.CountAsync(cancellationToken);
        int testSet = await db.HistoricalTickets.CountAsync(t => t.IsTestSet, cancellationToken);
        int messages = await db.TicketMessages.CountAsync(cancellationToken);
        int articles = await db.KbArticles.CountAsync(cancellationToken);
        int notes = await db.ReleaseNotes.CountAsync(cancellationToken);
        int chunks = await db.Chunks.CountAsync(cancellationToken);

        output.WriteLine();
        output.WriteLine($"support: prodotti {products} · versioni {versions} · clienti {customers}");
        output.WriteLine($"         ticket storici {tickets} (test set {testSet}) · messaggi {messages} · articoli KB {articles} · note di rilascio {notes}");
        output.WriteLine($"rag:     chunk {chunks} · colonna Embedding VECTOR({db.EmbeddingDimensions})");
    }
}
