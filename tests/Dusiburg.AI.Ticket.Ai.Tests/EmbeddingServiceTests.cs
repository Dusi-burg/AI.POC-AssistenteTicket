using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Dusiburg.AI.Ticket.Ai.Tests;

/// <summary>Prefissi, batch, retry e dimensione dei vettori (come <c>EmbeddingService</c> di Sinistri), con un generatore finto.</summary>
public class EmbeddingServiceTests
{
    [Test]
    public async Task EmbedDocuments_PrefixAndBatches()
    {
        //SETUP
        var generator = new FakeGenerator(dimensions: 768);
        EmbeddingService service = Create(generator, batchSize: 2);

        //SUT
        IReadOnlyList<float[]> vectors = await service.EmbedDocumentsAsync(["uno", "due", "tre"], CancellationToken.None);

        Assert.That(vectors, Has.Count.EqualTo(3));
        Assert.That(generator.Calls.Select(c => c.Count), Is.EqualTo(new[] { 2, 1 }));
        Assert.That(generator.Calls[0][0], Is.EqualTo("title: none | text: uno"));
    }

    [Test]
    public async Task EmbedQuery_UsesTheQueryPrefix()
    {
        //SETUP
        var generator = new FakeGenerator(dimensions: 768);
        EmbeddingService service = Create(generator);

        //SUT
        await service.EmbedQueryAsync("firma scaduta", CancellationToken.None);

        Assert.That(generator.Calls.Single().Single(), Is.EqualTo("task: search result | query: firma scaduta"));
    }

    [Test]
    public async Task EmbedQuery_TransientErrors_AreRetriedWithDelays()
    {
        //SETUP
        var generator = new FakeGenerator(dimensions: 768, failures: 2);
        List<TimeSpan> delays = [];
        EmbeddingService service = Create(generator, delay: (wait, _) => { delays.Add(wait); return Task.CompletedTask; });

        //SUT
        float[] vector = await service.EmbedQueryAsync("x", CancellationToken.None);

        Assert.That(vector, Has.Length.EqualTo(768));
        Assert.That(delays, Is.EqualTo(EmbeddingService.RetryDelays.Take(2)));
    }

    [Test]
    public void EmbedQuery_ClientError_IsNotRetried()
    {
        //SETUP
        var generator = new FakeGenerator(dimensions: 768, failures: 5, status: HttpStatusCode.NotFound);
        EmbeddingService service = Create(generator, delay: (_, _) => Task.CompletedTask);

        //SUT
        Assert.That(() => service.EmbedQueryAsync("x", CancellationToken.None), Throws.TypeOf<HttpRequestException>());
        Assert.That(generator.Calls, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task EmbedQuery_NativeVectorLongerThanConfigured_IsTruncatedAndNormalized()
    {
        //SETUP
        EmbeddingService service = Create(new FakeGenerator(dimensions: 768), dimensions: 256);

        //SUT
        float[] vector = await service.EmbedQueryAsync("x", CancellationToken.None);

        Assert.That(vector, Has.Length.EqualTo(256));
        Assert.That(MathF.Sqrt(vector.Sum(v => v * v)), Is.EqualTo(1f).Within(1e-5f));
    }

    [Test]
    public void EmbedQuery_UnexpectedDimension_Throws()
    {
        //SETUP
        EmbeddingService service = Create(new FakeGenerator(dimensions: 512));

        //SUT
        Assert.That(() => service.EmbedQueryAsync("x", CancellationToken.None),
            Throws.InvalidOperationException.With.Message.Contains("512").And.Message.Contains(AiOptions.Keys.EmbeddingDimensions));
    }

    [Test]
    public void Create_DimensionsAboveTheNativeOnes_Throws()
    {
        //SUT
        Assert.That(() => Create(new FakeGenerator(dimensions: 768), dimensions: 1024), Throws.InvalidOperationException.With.Message.Contains("768"));
    }

    private static EmbeddingService Create(
        FakeGenerator generator, int batchSize = 32, int dimensions = 768, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        AiOptions options = AiOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AiOptions.Keys.EmbeddingDimensions] = dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            .Build());

        return new EmbeddingService(generator, options, batchSize, delay ?? ((_, _) => Task.CompletedTask));
    }

    /// <summary>Vettori costanti della dimensione data; le prime <c>failures</c> chiamate falliscono con lo stato indicato.</summary>
    private sealed class FakeGenerator(int dimensions, int failures = 0, HttpStatusCode status = HttpStatusCode.ServiceUnavailable)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        private int _failures = failures;

        public List<List<string>> Calls { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            List<string> texts = [.. values];
            Calls.Add(texts);

            if (_failures-- > 0)
            {
                throw new HttpRequestException("errore finto", null, status);
            }

            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                texts.Select(_ => new Embedding<float>(Enumerable.Repeat(0.5f, dimensions).ToArray()))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
