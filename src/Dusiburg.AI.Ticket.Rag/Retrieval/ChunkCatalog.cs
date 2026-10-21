using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Rag.Retrieval;

/// <summary>Cambia quando cambia l'indice: numero e Id massimo dei chunk, ultimo aggiornamento dei vettori.</summary>
public sealed record CatalogStamp(int Count, int MaxId, DateTimeOffset? EmbeddedAt);

/// <summary>
/// I chunk in memoria con metadati e chiavi delle fonti, l'indice BM25 e le versioni dei prodotti: la ricerca vettoriale resta in
/// SQL, tutto il resto (filtri, fusione, penalità) lavora qui senza altre query.
/// </summary>
public sealed class ChunkCatalog
{
    private readonly Dictionary<int, IndexedChunk> _byId;
    private readonly Dictionary<string, short> _products;
    private readonly Dictionary<(short ProductId, string Version), short> _versions;

    public ChunkCatalog(
        CatalogStamp stamp,
        IReadOnlyList<IndexedChunk> chunks,
        IReadOnlyDictionary<string, short> products,
        IReadOnlyDictionary<(short ProductId, string Version), short> versionSequences)
    {
        Stamp = stamp;
        Chunks = chunks;
        _byId = chunks.ToDictionary(c => c.Id);
        _products = new Dictionary<string, short>(products, StringComparer.OrdinalIgnoreCase);
        _versions = new Dictionary<(short, string), short>(versionSequences);
        Bm25 = new Bm25Index(chunks);
    }

    public CatalogStamp Stamp { get; }

    public IReadOnlyList<IndexedChunk> Chunks { get; }

    public Bm25Index Bm25 { get; }

    public IndexedChunk this[int chunkId] => _byId[chunkId];

    public bool Contains(int chunkId) => _byId.ContainsKey(chunkId);

    /// <summary>Prodotto e versione non riconosciuti valgono come non noti (nessun filtro, nessuna penalità).</summary>
    public AppliedFilters Resolve(RetrievalQuery query)
    {
        short? productId = query.ProductCode is { } code && _products.TryGetValue(code.Trim(), out short id) ? id : null;
        short? sequence = productId is { } product && query.Version is { } version && _versions.TryGetValue((product, version.Trim()), out short s)
            ? s
            : null;

        return new AppliedFilters(productId, query.Mode, sequence);
    }

    /// <summary>Stesso prodotto; modalità uguale o chunk valido per entrambe.</summary>
    public static bool Matches(IndexedChunk chunk, AppliedFilters filters) =>
        (filters.ProductId is null || chunk.ProductId == filters.ProductId)
        && (filters.Mode is null || chunk.Mode is null || chunk.Mode == filters.Mode);

    public static async Task<CatalogStamp> ReadStampAsync(TicketDbContext db, CancellationToken cancellationToken)
    {
        var counts = await db.Chunks
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), MaxId = g.Max(c => c.Id) })
            .SingleOrDefaultAsync(cancellationToken);
        DateTimeOffset? embeddedAt = await db.EmbeddingInfo.Select(i => (DateTimeOffset?)i.UpdatedAt).SingleOrDefaultAsync(cancellationToken);

        return new CatalogStamp(counts?.Count ?? 0, counts?.MaxId ?? 0, embeddedAt);
    }

    public static async Task<ChunkCatalog> LoadAsync(TicketDbContext db, CancellationToken cancellationToken)
    {
        CatalogStamp stamp = await ReadStampAsync(db, cancellationToken);
        Dictionary<int, short> sequences = await db.ProductVersions.AsNoTracking().ToDictionaryAsync(v => v.Id, v => v.Sequence, cancellationToken);
        Dictionary<int, string> tickets = await db.HistoricalTickets.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Number, cancellationToken);
        Dictionary<int, string> articles = await db.KbArticles.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code, cancellationToken);
        Dictionary<int, string> notes = await db.ReleaseNotes.AsNoTracking().ToDictionaryAsync(n => n.Id, n => n.Code, cancellationToken);

        var rows = await db.Chunks.AsNoTracking()
            .OrderBy(c => c.Id)
            .Select(c => new
            {
                c.Id, c.Source, c.HistoricalTicketId, c.KbArticleId, c.ReleaseNoteId, c.Section, c.ProductId, c.DeploymentMode,
                c.VersionFromId, c.VersionToId, c.Text
            })
            .ToListAsync(cancellationToken);

        List<IndexedChunk> chunks = [.. rows.Select(r => new IndexedChunk(
            r.Id,
            r.Source,
            r.Source switch
            {
                ChunkSource.HistoricalTicket => tickets[r.HistoricalTicketId!.Value],
                ChunkSource.KbArticle => articles[r.KbArticleId!.Value],
                ChunkSource.ReleaseNote => notes[r.ReleaseNoteId!.Value],
                _ => throw new InvalidOperationException($"Chunk {r.Id} con fonte {r.Source} sconosciuta.")
            },
            r.Section,
            r.ProductId,
            r.DeploymentMode,
            r.VersionFromId is { } from ? sequences[from] : null,
            r.VersionToId is { } to ? sequences[to] : null,
            r.Text))];

        Dictionary<string, short> products = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Code, p => p.Id, cancellationToken);
        Dictionary<(short, string), short> versions = (await db.ProductVersions.AsNoTracking().ToListAsync(cancellationToken))
            .ToDictionary(v => (v.ProductId, v.Version), v => v.Sequence);

        return new ChunkCatalog(stamp, chunks, products, versions);
    }
}

/// <summary>
/// Il catalogo si carica una volta e si ricarica solo quando cambia l'indice (<see cref="CatalogStamp"/>, una query leggera per
/// ricerca): singleton nell'Agent, mentre il contesto EF resta scoped.
/// </summary>
public sealed class ChunkCatalogCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ChunkCatalog? _current;

    public async Task<ChunkCatalog> GetAsync(TicketDbContext db, CancellationToken cancellationToken)
    {
        CatalogStamp stamp = await ChunkCatalog.ReadStampAsync(db, cancellationToken);

        if (_current is { } current && current.Stamp == stamp)
        {
            return current;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_current is null || _current.Stamp != stamp)
            {
                _current = await ChunkCatalog.LoadAsync(db, cancellationToken);
            }

            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }
}
