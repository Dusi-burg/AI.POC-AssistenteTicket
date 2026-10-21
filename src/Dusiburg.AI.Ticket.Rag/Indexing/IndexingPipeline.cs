using System.Diagnostics;
using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Shared.Domain;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Rag.Indexing;

/// <summary><c>embed</c> completo (ricostruisce i chunk) o <c>--solo-mancanti</c> (aggiunge chunk e vettori che mancano).</summary>
public sealed record EmbedRequest(bool OnlyMissing);

public sealed record EmbedProgress(int Done, int Total);

public sealed record EmbedResult(
    int ChunksRemoved,
    int ChunksCreated,
    int Vectors,
    IReadOnlyDictionary<ChunkSource, int> ChunksBySource,
    int WithoutEmbedding,
    TimeSpan Duration);

/// <summary>
/// Costruzione dei chunk e calcolo dei vettori (fase-5.md §4), sul modello di <c>EmbeddingPipeline</c> di Sinistri: pagine per
/// chiave, un batch per salvataggio, <c>EmbeddingInfo</c> scritta a fine lavoro. Se un batch fallisce dopo i retry il comando si
/// ferma e i vettori già scritti restano, così <c>--solo-mancanti</c> riprende da lì. Le scritture sono EF (G4): Dapper serve
/// solo alla ricerca vettoriale.
/// </summary>
public sealed class IndexingPipeline(TicketDbContext db, IEmbeddingService embeddings, AiOptions options, TimeProvider timeProvider)
{
    public const int PageSize = 200;

    public async Task<EmbedResult> RunAsync(EmbedRequest request, IProgress<EmbedProgress>? progress, CancellationToken cancellationToken)
    {
        await VerifyAsync(request, cancellationToken);

        var watch = Stopwatch.StartNew();
        int removed = 0;

        if (!request.OnlyMissing)
        {
            // Le fonti citate dalle bozze restano leggibili: AgentDraftSource.ChunkId diventa NULL (FK ON DELETE SET NULL), SourceKey resta.
            removed = await db.Chunks.ExecuteDeleteAsync(cancellationToken);
            await db.EmbeddingInfo.ExecuteDeleteAsync(cancellationToken);
        }

        int created = await CreateMissingChunksAsync(cancellationToken);
        int vectors = await EmbedMissingAsync(progress, cancellationToken);

        if (vectors > 0)
        {
            await WriteEmbeddingInfoAsync(cancellationToken);
        }

        Dictionary<ChunkSource, int> bySource = await db.Chunks
            .GroupBy(c => c.Source)
            .Select(g => new { Source = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Source, g => g.Count, cancellationToken);
        int withoutEmbedding = await db.Chunks.CountAsync(c => c.Embedding == null, cancellationToken);

        return new EmbedResult(removed, created, vectors, bySource, withoutEmbedding, watch.Elapsed);
    }

    /// <summary>
    /// Colonna della dimensione configurata; con vettori già calcolati da un altro modello niente lavoro parziale: vecchi e nuovi
    /// non sarebbero confrontabili.
    /// </summary>
    private async Task VerifyAsync(EmbedRequest request, CancellationToken cancellationToken)
    {
        int? column = await db.Database
            .SqlQuery<int?>($"SELECT CAST(vector_dimensions AS int) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'rag.Chunk') AND name = N'Embedding'")
            .SingleOrDefaultAsync(cancellationToken);

        if (column != options.EmbeddingDimensions)
        {
            throw new InvalidOperationException(
                $"rag.Chunk.Embedding è {(column is null ? "assente" : $"VECTOR({column})")} ma {AiOptions.Keys.EmbeddingDimensions}={options.EmbeddingDimensions}: rieseguire DbInit.");
        }

        EmbeddingInfo? info = await db.EmbeddingInfo.AsNoTracking().SingleOrDefaultAsync(cancellationToken);

        if (request.OnlyMissing && info is not null && (info.Model != options.EmbeddingModel || info.Dimensions != options.EmbeddingDimensions))
        {
            throw new InvalidOperationException(
                $"Gli embedding salvati sono di {info.Model} ({info.Dimensions}) ma la configurazione usa {options.EmbeddingModel} " +
                $"({options.EmbeddingDimensions}): i vettori non sarebbero confrontabili. Rieseguire embed senza --solo-mancanti.");
        }
    }

    private async Task<int> CreateMissingChunksAsync(CancellationToken cancellationToken)
    {
        var existing = (await db.Chunks
                .Select(c => new { c.Source, SourceId = c.HistoricalTicketId ?? c.KbArticleId ?? c.ReleaseNoteId, c.Section })
                .ToListAsync(cancellationToken))
            .Select(c => (c.Source, c.SourceId!.Value, c.Section))
            .ToHashSet();

        List<Chunk> missing = [.. (await BuildAllAsync(cancellationToken)).Where(c => !existing.Contains((c.Source, SourceId(c), c.Section)))];

        db.Chunks.AddRange(missing);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        return missing.Count;
    }

    private async Task<IReadOnlyList<Chunk>> BuildAllAsync(CancellationToken cancellationToken)
    {
        Dictionary<int, string> customers = await db.Customers.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        List<HistoricalTicket> tickets = await db.HistoricalTickets.AsNoTracking()
            .Where(t => !t.IsTestSet && t.State == TicketState.Resolved && t.DuplicateOfId == null && t.Resolution != null)
            .Include(t => t.Messages)
            .Include(t => t.Resolution)
            .AsSplitQuery()
            .OrderBy(t => t.Id)
            .ToListAsync(cancellationToken);
        List<KbArticle> articles = await db.KbArticles.AsNoTracking().OrderBy(a => a.Id).ToListAsync(cancellationToken);
        List<ReleaseNote> notes = await db.ReleaseNotes.AsNoTracking().OrderBy(n => n.Id).ToListAsync(cancellationToken);
        List<ProductVersion> versions = await db.ProductVersions.AsNoTracking().ToListAsync(cancellationToken);
        Dictionary<short, string> products = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);

        return
        [
            .. tickets.Select(t => ChunkBuilder.ForTicket(t, customers[t.CustomerId])),
            .. articles.SelectMany(ChunkBuilder.ForKbArticle),
            .. notes.Select(note =>
            {
                ProductVersion version = versions.Single(v => v.Id == note.ProductVersionId);
                ProductVersion? previous = versions.SingleOrDefault(v => v.ProductId == version.ProductId && v.Sequence == version.Sequence - 1);

                return ChunkBuilder.ForReleaseNote(note, version, previous, products[version.ProductId]);
            })
        ];
    }

    private async Task<int> EmbedMissingAsync(IProgress<EmbedProgress>? progress, CancellationToken cancellationToken)
    {
        int total = await db.Chunks.CountAsync(c => c.Embedding == null, cancellationToken);
        int done = 0;
        int afterId = 0;

        progress?.Report(new EmbedProgress(0, total));

        while (true)
        {
            List<Chunk> page = await db.Chunks
                .Where(c => c.Embedding == null && c.Id > afterId)
                .OrderBy(c => c.Id)
                .Take(PageSize)
                .ToListAsync(cancellationToken);

            if (page.Count == 0)
            {
                break;
            }

            foreach (Chunk[] batch in page.Chunk(EmbeddingService.DefaultBatchSize))
            {
                IReadOnlyList<float[]> vectors = await embeddings.EmbedDocumentsAsync([.. batch.Select(c => c.Text)], cancellationToken);

                for (int i = 0; i < batch.Length; i++)
                {
                    batch[i].Embedding = new SqlVector<float>(vectors[i]);
                }

                await db.SaveChangesAsync(cancellationToken);

                done += batch.Length;
                progress?.Report(new EmbedProgress(done, total));
            }

            afterId = page[^1].Id;
            db.ChangeTracker.Clear();
        }

        return done;
    }

    private async Task WriteEmbeddingInfoAsync(CancellationToken cancellationToken)
    {
        EmbeddingInfo? info = await db.EmbeddingInfo.SingleOrDefaultAsync(cancellationToken);

        if (info is null)
        {
            info = new EmbeddingInfo { Model = options.EmbeddingModel };
            db.EmbeddingInfo.Add(info);
        }

        info.Model = options.EmbeddingModel;
        info.Dimensions = (short)options.EmbeddingDimensions;
        info.UpdatedAt = timeProvider.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    private static int SourceId(Chunk chunk) =>
        chunk.HistoricalTicketId ?? chunk.KbArticleId ?? chunk.ReleaseNoteId ?? throw new InvalidOperationException("Chunk senza fonte.");
}
