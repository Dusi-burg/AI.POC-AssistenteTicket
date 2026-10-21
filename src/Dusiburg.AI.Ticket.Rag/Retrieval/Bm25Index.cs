namespace Dusiburg.AI.Ticket.Rag.Retrieval;

/// <summary>
/// Componente lessicale di P5: BM25 in memoria sui chunk del catalogo (LocalDB non ha la ricerca full-text). Con ~900 chunk
/// l'indice si costruisce in pochi millisecondi e una ricerca è sotto il millisecondo. IDF calcolato sull'intero corpus, filtri
/// (prodotto, modalità) applicati ai documenti candidati.
/// </summary>
public sealed class Bm25Index
{
    public const double DefaultK1 = 1.2;
    public const double DefaultB = 0.75;

    private readonly IReadOnlyList<IndexedChunk> _chunks;
    private readonly Dictionary<string, List<(int Document, int Frequency)>> _postings = new(StringComparer.Ordinal);
    private readonly int[] _lengths;
    private readonly double _averageLength;
    private readonly double _k1;
    private readonly double _b;

    public Bm25Index(IReadOnlyList<IndexedChunk> chunks, double k1 = DefaultK1, double b = DefaultB)
    {
        _chunks = chunks;
        _k1 = k1;
        _b = b;
        _lengths = new int[chunks.Count];

        for (int document = 0; document < chunks.Count; document++)
        {
            IReadOnlyList<string> tokens = ItalianTokenizer.Tokenize(chunks[document].Text);
            _lengths[document] = tokens.Count;

            foreach (IGrouping<string, string> term in tokens.GroupBy(t => t, StringComparer.Ordinal))
            {
                if (!_postings.TryGetValue(term.Key, out List<(int Document, int Frequency)>? list))
                {
                    list = [];
                    _postings[term.Key] = list;
                }

                list.Add((document, term.Count()));
            }
        }

        _averageLength = chunks.Count == 0 ? 0 : _lengths.Average();
    }

    public int Count => _chunks.Count;

    /// <summary>I primi <paramref name="top"/> chunk che passano il filtro, per punteggio decrescente (a parità, per Id).</summary>
    public IReadOnlyList<(IndexedChunk Chunk, double Score)> Search(string query, Func<IndexedChunk, bool> filter, int top)
    {
        Dictionary<int, double> scores = [];

        foreach (string term in ItalianTokenizer.Tokenize(query).Distinct(StringComparer.Ordinal))
        {
            if (!_postings.TryGetValue(term, out List<(int Document, int Frequency)>? postings))
            {
                continue;
            }

            double idf = Math.Log(1 + (_chunks.Count - postings.Count + 0.5) / (postings.Count + 0.5));

            foreach ((int document, int frequency) in postings)
            {
                double normalization = _k1 * (1 - _b + _b * _lengths[document] / _averageLength);
                scores[document] = scores.GetValueOrDefault(document) + idf * frequency * (_k1 + 1) / (frequency + normalization);
            }
        }

        return [.. scores
            .Where(score => filter(_chunks[score.Key]))
            .OrderByDescending(score => score.Value)
            .ThenBy(score => _chunks[score.Key].Id)
            .Take(top)
            .Select(score => (_chunks[score.Key], score.Value))];
    }
}
