using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Rag.Retrieval;

/// <summary>
/// Fusione delle classifiche (P5), pura: Reciprocal Rank Fusion, penalità di versione, raggruppamento per fonte. Una variante
/// singola (solo vettoriale o solo BM25) passa dallo stesso calcolo con l'altra classifica vuota, così i punteggi restano
/// confrontabili nella valutazione.
/// </summary>
public static class RankFusion
{
    public static IReadOnlyList<RetrievedChunk> Fuse(
        IReadOnlyList<(IndexedChunk Chunk, double Distance)> vector,
        IReadOnlyList<(IndexedChunk Chunk, double Score)> bm25,
        short? versionSequence,
        RetrievalOptions options,
        bool applyVersionPenalty)
    {
        Dictionary<int, (IndexedChunk Chunk, int? VectorRank, double? Distance, int? Bm25Rank, double? Bm25Score)> merged = [];

        for (int i = 0; i < vector.Count; i++)
        {
            merged[vector[i].Chunk.Id] = (vector[i].Chunk, i + 1, vector[i].Distance, null, null);
        }

        for (int i = 0; i < bm25.Count; i++)
        {
            (IndexedChunk chunk, double score) = bm25[i];
            merged[chunk.Id] = merged.TryGetValue(chunk.Id, out var found)
                ? found with { Bm25Rank = i + 1, Bm25Score = score }
                : (chunk, null, null, i + 1, score);
        }

        return [.. merged.Values
            .Select(m =>
            {
                double rrf = Reciprocal(m.VectorRank, options.RrfK) + Reciprocal(m.Bm25Rank, options.RrfK);
                bool penalized = applyVersionPenalty && versionSequence is { } version && !m.Chunk.AppliesTo(version);

                return new RetrievedChunk(m.Chunk, m.VectorRank, m.Distance, m.Bm25Rank, m.Bm25Score,
                    penalized ? rrf * options.VersionPenalty : rrf, penalized);
            })
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.VectorRank ?? int.MaxValue)
            .ThenBy(c => c.Chunk.Id)
            .Take(options.TopK)];
    }

    /// <summary>Una riga per fonte, col punteggio del suo chunk migliore, nell'ordine dei chunk.</summary>
    public static IReadOnlyList<RetrievedSource> GroupBySource(IReadOnlyList<RetrievedChunk> chunks) =>
        [.. chunks
            .GroupBy(c => (c.Chunk.Source, c.Chunk.SourceKey))
            .Select((group, index) => new RetrievedSource(index + 1, group.Key.Source, group.Key.SourceKey, group.First().Score, group.First(), [.. group]))];

    /// <summary>
    /// Le fonti del prompt (<c>PROMPT_SOURCES_MAX</c>): le prime in classifica, ma con almeno un documento (articolo KB o nota di
    /// rilascio) se uno compare nel top-K. I ticket simili dello stesso problema tendono a occupare tutti i posti, mentre la
    /// procedura ufficiale e la nota del bug sono ciò che la bozza deve citare (scenario 4, PLAN §6.4); come le clausole integrative
    /// di Sinistri (D10), il documento prende l'ultimo posto.
    /// </summary>
    public static IReadOnlyList<RetrievedSource> SelectForPrompt(IReadOnlyList<RetrievedSource> sources, int max)
    {
        List<RetrievedSource> selected = [.. sources.Take(max)];

        if (selected.Count == max && selected.All(s => s.Source == ChunkSource.HistoricalTicket)
            && sources.Skip(max).FirstOrDefault(s => s.Source != ChunkSource.HistoricalTicket) is { } document)
        {
            selected[^1] = document;
        }

        return selected;
    }

    private static double Reciprocal(int? rank, int k) => rank is { } r ? 1.0 / (k + r) : 0;
}
