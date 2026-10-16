using System.Net;
using Microsoft.Extensions.AI;

namespace Dusiburg.AI.Ticket.Ai;

/// <summary>Vettori di query e documenti con i prefissi del modello: chi indicizza e chi cerca non conosce il profilo.</summary>
public interface IEmbeddingService
{
    EmbeddingProfile Profile { get; }

    Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken);

    Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IEmbeddingService"/> sul generatore di <see cref="EmbeddingGeneratorFactory"/>, come <c>EmbeddingService</c> di
/// Sinistri: applica il prefisso del profilo, spezza in batch, riprova sugli errori transitori, riduce i vettori con dimensione
/// nativa maggiore (troncamento MRL con rinormalizzazione) e controlla la dimensione di ogni vettore.
/// </summary>
public sealed class EmbeddingService : IEmbeddingService
{
    /// <summary>Testi per richiesta a Ollama: su CPU un batch più grande non accorcia i tempi e allunga l'attesa del primo vettore.</summary>
    public const int DefaultBatchSize = 32;

    /// <summary>Attese tra i tentativi: 3 tentativi in tutto dopo il primo errore, ciclo esplicito senza librerie di resilienza.</summary>
    internal static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9)];

    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly AiOptions _options;
    private readonly int _batchSize;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public EmbeddingService(EmbeddingGeneratorFactory factory, AiOptions options)
        : this(factory.Create(), options, DefaultBatchSize, Task.Delay)
    {
    }

    internal EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> generator, AiOptions options, int batchSize, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _generator = generator;
        _options = options;
        _batchSize = batchSize;
        _delay = delay;
        Profile = EmbeddingProfile.ForModel(options.EmbeddingModel);

        if (options.EmbeddingDimensions > Profile.NativeDimensions)
        {
            throw new InvalidOperationException(
                $"{AiOptions.Keys.EmbeddingDimensions}={options.EmbeddingDimensions} supera la dimensione nativa di {Profile.Model} ({Profile.NativeDimensions}).");
        }
    }

    public EmbeddingProfile Profile { get; }

    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken) =>
        (await GenerateAsync([Profile.Query(text)], cancellationToken))[0];

    public async Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        List<float[]> vectors = new(texts.Count);

        foreach (string[] batch in texts.Select(Profile.Document).Chunk(_batchSize))
        {
            vectors.AddRange(await GenerateAsync(batch, cancellationToken));
        }

        return vectors;
    }

    private async Task<IReadOnlyList<float[]>> GenerateAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        GeneratedEmbeddings<Embedding<float>> embeddings = await WithRetryAsync(
            () => _generator.GenerateAsync(texts, cancellationToken: cancellationToken), cancellationToken);

        if (embeddings.Count != texts.Count)
        {
            throw new InvalidOperationException($"{Description()}: {embeddings.Count} vettori per {texts.Count} testi.");
        }

        return [.. embeddings.Select(e => Reduce(e.Vector.ToArray()))];
    }

    /// <summary>Troncamento MRL alla dimensione usata e rinormalizzazione; poi il controllo di dimensione.</summary>
    private float[] Reduce(float[] vector)
    {
        if (vector.Length > _options.EmbeddingDimensions && vector.Length == Profile.NativeDimensions)
        {
            vector = vector[.._options.EmbeddingDimensions];
            float norm = MathF.Sqrt(vector.Sum(v => v * v));

            if (norm > 0)
            {
                vector = [.. vector.Select(v => v / norm)];
            }
        }

        return vector.Length == _options.EmbeddingDimensions
            ? vector
            : throw new InvalidOperationException(
                $"{Description()}: vettore di {vector.Length} elementi, attesi {_options.EmbeddingDimensions} ({AiOptions.Keys.EmbeddingDimensions}).");
    }

    private async Task<T> WithRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception exception) when (attempt < RetryDelays.Length && IsTransient(exception, cancellationToken))
            {
                await _delay(RetryDelays[attempt], cancellationToken);
            }
        }
    }

    /// <summary>Errori di rete, timeout e risposte 5xx; mai i 4xx (es. modello inesistente) né l'annullamento chiesto dal chiamante.</summary>
    internal static bool IsTransient(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        HttpRequestException { StatusCode: { } status } => (int)status >= 500 || status == HttpStatusCode.RequestTimeout,
        HttpRequestException => true,
        TimeoutException => true,
        TaskCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false
    };

    private string Description() => $"embedding {_options.EmbeddingModel}";
}
