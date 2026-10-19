using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Rag.Retrieval;

namespace Dusiburg.AI.Ticket.Core.Tests.Support;

/// <summary>
/// Embedding finto e deterministico per i test su database (G12, niente modelli reali): i token del testo distribuiti su
/// <c>dimensions</c> componenti con un hash stabile, poi normalizzati. Testi con parole in comune risultano vicini.
/// </summary>
internal sealed class HashingEmbeddingService(int dimensions) : IEmbeddingService
{
    public EmbeddingProfile Profile { get; } = EmbeddingProfile.ForModel(AiOptions.DefaultEmbeddingModel);

    public int Calls { get; private set; }

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken)
    {
        Calls++;

        return Task.FromResult(Vector(text));
    }

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        Calls++;

        return Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(Vector)]);
    }

    private float[] Vector(string text)
    {
        float[] vector = new float[dimensions];

        foreach (string token in ItalianTokenizer.Tokenize(text))
        {
            vector[StableHash(token) % dimensions] += 1;
        }

        float norm = MathF.Sqrt(vector.Sum(v => v * v));

        return norm == 0 ? [1, .. new float[dimensions - 1]] : [.. vector.Select(v => v / norm)];
    }

    private static int StableHash(string token) => token.Aggregate(17, (hash, c) => unchecked((hash * 31) + c)) & int.MaxValue;
}
