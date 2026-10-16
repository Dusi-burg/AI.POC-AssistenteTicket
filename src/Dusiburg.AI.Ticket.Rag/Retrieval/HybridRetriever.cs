using System.Diagnostics;
using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Shared.Telemetry;

namespace Dusiburg.AI.Ticket.Rag.Retrieval;

/// <summary>
/// Retrieval ibrido di P5 (fase-5.md §5): vettoriale in SQL e BM25 in memoria sugli stessi filtri (prodotto, modalità), fusione
/// RRF, penalità per i chunk fuori dall'intervallo di versione del cliente, raggruppamento per fonte. Uno span per ricerca nel
/// dashboard di Aspire.
/// </summary>
public sealed class HybridRetriever(TicketDbContext db, IEmbeddingService embeddings, ChunkCatalogCache cache, RetrievalOptions options)
{
    private static readonly ActivitySource Telemetry = new(TicketTelemetry.Sources.Retrieval);

    public RetrievalOptions Options => options;

    public Task<RetrievalResult> SearchAsync(RetrievalQuery query, CancellationToken cancellationToken) =>
        SearchAsync(query, queryVector: null, RetrievalSettings.Default, cancellationToken);

    /// <summary>
    /// Con <paramref name="queryVector"/> già calcolato (la valutazione prova più varianti sulla stessa query) l'embedding non si
    /// ripete; la variante BM25 non lo calcola affatto.
    /// </summary>
    public async Task<RetrievalResult> SearchAsync(
        RetrievalQuery query, float[]? queryVector, RetrievalSettings settings, CancellationToken cancellationToken)
    {
        using Activity? activity = Telemetry.StartActivity("retrieval");
        long start = Stopwatch.GetTimestamp();

        ChunkCatalog catalog = await cache.GetAsync(db, cancellationToken);
        AppliedFilters filters = catalog.Resolve(query);

        TimeSpan embeddingTime = TimeSpan.Zero;
        TimeSpan vectorTime = TimeSpan.Zero;
        List<(IndexedChunk Chunk, double Distance)> vector = [];

        if (settings.Variant != RetrievalVariant.Bm25)
        {
            long embeddingStart = Stopwatch.GetTimestamp();
            queryVector ??= await embeddings.EmbedQueryAsync(query.Text, cancellationToken);
            embeddingTime = Stopwatch.GetElapsedTime(embeddingStart);

            long vectorStart = Stopwatch.GetTimestamp();
            IReadOnlyList<VectorHit> hits = await new VectorSearch(db).SearchAsync(queryVector, filters, options.TopK, cancellationToken);
            vectorTime = Stopwatch.GetElapsedTime(vectorStart);

            // Un chunk aggiunto dopo il caricamento del catalogo arriva alla ricerca successiva.
            vector.AddRange(hits.Where(h => catalog.Contains(h.ChunkId)).Select(h => (catalog[h.ChunkId], h.Distance)));
        }

        long bm25Start = Stopwatch.GetTimestamp();
        IReadOnlyList<(IndexedChunk Chunk, double Score)> lexical = settings.Variant == RetrievalVariant.Vector
            ? []
            : catalog.Bm25.Search(query.Text, chunk => ChunkCatalog.Matches(chunk, filters), options.TopK);
        TimeSpan bm25Time = Stopwatch.GetElapsedTime(bm25Start);

        IReadOnlyList<RetrievedChunk> chunks = RankFusion.Fuse(vector, lexical, filters.VersionSequence, options, settings.ApplyVersionPenalty);
        IReadOnlyList<RetrievedSource> sources = RankFusion.GroupBySource(chunks);
        IReadOnlyList<RetrievedSource> prompt = RankFusion.SelectForPrompt(sources, options.PromptSourcesMax);

        activity?.SetTag("retrieval.variant", settings.Variant.ToString());
        activity?.SetTag("retrieval.product_id", filters.ProductId);
        activity?.SetTag("retrieval.mode", filters.Mode?.ToString());
        activity?.SetTag("retrieval.version_sequence", filters.VersionSequence);
        activity?.SetTag("retrieval.sources", string.Join(",", prompt.Select(s => s.Key)));

        return new RetrievalResult(chunks, sources, prompt, filters, new RetrievalTimings(embeddingTime, vectorTime, bm25Time, Stopwatch.GetElapsedTime(start)));
    }
}
