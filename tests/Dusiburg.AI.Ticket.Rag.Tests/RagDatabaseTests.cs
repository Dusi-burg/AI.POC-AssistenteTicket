using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.DbInit;
using Dusiburg.AI.Ticket.Rag.Indexing;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Rag.Tests.Support;
using Dusiburg.AI.Ticket.Shared.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Dusiburg.AI.Ticket.Rag.Tests;

/// <summary>
/// Indicizzazione e ricerca su un database dedicato di <c>(localdb)\localdev</c> con <c>VECTOR(4)</c> e i dati di <c>data/</c>,
/// con un embedding finto (G12). La colonna <c>VECTOR</c> esiste solo da SQL Server 2025, che la CI può non avere.
/// </summary>
[Category("RequiresSql2025")]
public class RagDatabaseTests
{
    private const int Dimensions = 4;

    private string _connectionString = null!;
    private EmbedResult _firstRun = null!;

    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [OneTimeSetUp]
    public async Task CreateAndIndexDatabase()
    {
        _connectionString = TicketDatabaseInitializer.LocalConnectionStringFor($"Ticket_Test_{Guid.NewGuid():N}");
        await TicketDatabaseInitializer.RecreateAsync(_connectionString, Dimensions, seed: true, CancellationToken);

        await using TicketDbContext db = Open();
        _firstRun = await Pipeline(db).RunAsync(new EmbedRequest(OnlyMissing: false), null, CancellationToken);
    }

    [OneTimeTearDown]
    public async Task DropDatabase() => await TicketDatabaseInitializer.DropAsync(_connectionString);

    [Test]
    public async Task Embed_Full_ChunksForEverySourceAllWithVectors()
    {
        //SETUP
        await using TicketDbContext db = Open();
        int indexable = await db.HistoricalTickets.CountAsync(
            t => !t.IsTestSet && t.State == TicketState.Resolved && t.DuplicateOfId == null && t.Resolution != null, CancellationToken);
        int notes = await db.ReleaseNotes.CountAsync(CancellationToken);

        //SUT
        EmbeddingInfo info = await db.EmbeddingInfo.SingleAsync(CancellationToken);

        Assert.That(_firstRun.ChunksBySource[ChunkSource.HistoricalTicket], Is.EqualTo(indexable));
        Assert.That(_firstRun.ChunksBySource[ChunkSource.ReleaseNote], Is.EqualTo(notes));
        Assert.That(_firstRun.ChunksBySource[ChunkSource.KbArticle], Is.GreaterThan(await db.KbArticles.CountAsync(CancellationToken)));
        Assert.That(_firstRun.Vectors, Is.EqualTo(_firstRun.ChunksCreated));
        Assert.That(_firstRun.WithoutEmbedding, Is.Zero);
        Assert.That((info.Model, info.Dimensions), Is.EqualTo((AiOptions.DefaultEmbeddingModel, (short)Dimensions)));
        Assert.That(await db.Chunks.AnyAsync(c => c.HistoricalTicketId != null && db.HistoricalTickets.Any(t => t.Id == c.HistoricalTicketId && t.IsTestSet), CancellationToken), Is.False);
    }

    [Test]
    public async Task Embed_OnlyMissing_RecomputesOnlyTheNullVectors()
    {
        //SETUP
        await using TicketDbContext db = Open();
        int[] ids = await db.Chunks.OrderBy(c => c.Id).Select(c => c.Id).Take(3).ToArrayAsync(CancellationToken);
        await db.Chunks.Where(c => ids.Contains(c.Id)).ExecuteUpdateAsync(s => s.SetProperty(c => c.Embedding, (Microsoft.Data.SqlTypes.SqlVector<float>?)null), CancellationToken);

        //SUT
        EmbedResult result = await Pipeline(db).RunAsync(new EmbedRequest(OnlyMissing: true), null, CancellationToken);

        Assert.That((result.ChunksRemoved, result.ChunksCreated, result.Vectors, result.WithoutEmbedding), Is.EqualTo((0, 0, 3, 0)));
    }

    [Test]
    public async Task Embed_OnlyMissingWithVectorsOfAnotherModel_IsRefused()
    {
        //SETUP
        await using TicketDbContext db = Open();
        await db.EmbeddingInfo.ExecuteUpdateAsync(s => s.SetProperty(i => i.Model, "bge-m3"), CancellationToken);

        try
        {
            //SUT
            Assert.That(() => Pipeline(db).RunAsync(new EmbedRequest(OnlyMissing: true), null, CancellationToken),
                Throws.InvalidOperationException.With.Message.Contains("bge-m3").And.Message.Contains("--solo-mancanti"));
        }
        finally
        {
            await db.EmbeddingInfo.ExecuteUpdateAsync(s => s.SetProperty(i => i.Model, AiOptions.DefaultEmbeddingModel), CancellationToken);
        }
    }

    [Test]
    public async Task Embed_ColumnOfAnotherDimension_IsRefused()
    {
        //SETUP
        await using TicketDbContext db = Open();
        var pipeline = new IndexingPipeline(db, new HashingEmbeddingService(768), Options(768), TimeProvider.System);

        //SUT
        Assert.That(() => pipeline.RunAsync(new EmbedRequest(OnlyMissing: false), null, CancellationToken),
            Throws.InvalidOperationException.With.Message.Contains("VECTOR(4)").And.Message.Contains("DbInit"));
    }

    [Test]
    public async Task VectorSearch_ProductAndMode_FilterTheChunksInDistanceOrder()
    {
        //SETUP
        await using TicketDbContext db = Open();
        short docuArchive = await db.Products.Where(p => p.Code == "DA").Select(p => p.Id).SingleAsync(CancellationToken);
        float[] query = await new HashingEmbeddingService(Dimensions).EmbedQueryAsync("la ricerca non trova i documenti", CancellationToken);

        //SUT
        IReadOnlyList<VectorHit> hits = await new VectorSearch(db).SearchAsync(query, new AppliedFilters(docuArchive, DeploymentMode.Saas, null), 15, CancellationToken);

        int[] ids = [.. hits.Select(h => h.ChunkId)];
        List<Chunk> chunks = await db.Chunks.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(CancellationToken);
        Assert.That(hits, Has.Count.EqualTo(15));
        Assert.That(hits.Select(h => h.Distance), Is.Ordered);
        Assert.That(chunks, Has.All.Matches<Chunk>(c => c.ProductId == docuArchive && (c.DeploymentMode == null || c.DeploymentMode == DeploymentMode.Saas)));
    }

    [Test]
    public async Task HybridRetriever_ErrorCodeQuery_FindsTheKbArticleOfTheCode()
    {
        //SETUP
        await using TicketDbContext db = Open();
        var retriever = new HybridRetriever(db, new HashingEmbeddingService(Dimensions), new ChunkCatalogCache(), RetrievalOptions.Default);
        var query = new RetrievalQuery("Invio bloccato: errore FF-E2041 handshake TLS rifiutato", "FF", DeploymentMode.OnPrem, "5.1.2");

        //SUT
        RetrievalResult result = await retriever.SearchAsync(query, CancellationToken);

        Assert.That(result.Sources.Take(5).Select(s => s.Key), Does.Contain("KB-FF-0004"));
        Assert.That(result.Filters.ProductId, Is.Not.Null);
        Assert.That(result.Filters.VersionSequence, Is.Not.Null);
        Assert.That(result.Chunks, Has.Count.LessThanOrEqualTo(RetrievalOptions.Default.TopK));
        Assert.That(result.Sources.Select(s => s.Key), Is.Unique);
    }

    [Test]
    public async Task ChunkCatalogCache_ReloadsOnlyWhenTheIndexChanges()
    {
        //SETUP
        await using TicketDbContext db = Open();
        var cache = new ChunkCatalogCache();

        //SUT
        ChunkCatalog first = await cache.GetAsync(db, CancellationToken);
        ChunkCatalog same = await cache.GetAsync(db, CancellationToken);
        await Pipeline(db).RunAsync(new EmbedRequest(OnlyMissing: false), null, CancellationToken);
        ChunkCatalog reloaded = await cache.GetAsync(db, CancellationToken);

        Assert.That(same, Is.SameAs(first));
        Assert.That(reloaded, Is.Not.SameAs(first));
        Assert.That(reloaded.Chunks, Has.Count.EqualTo(first.Chunks.Count));
        Assert.That(reloaded.Chunks.Select(c => c.Id).Min(), Is.GreaterThan(first.Chunks.Select(c => c.Id).Max()));
    }

    private TicketDbContext Open() => TicketDatabaseInitializer.Create(_connectionString, Dimensions);

    private static IndexingPipeline Pipeline(TicketDbContext db) =>
        new(db, new HashingEmbeddingService(Dimensions), Options(Dimensions), TimeProvider.System);

    private static AiOptions Options(int dimensions) => AiOptions.FromConfiguration(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [AiOptions.Keys.EmbeddingDimensions] = dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        .Build());
}
