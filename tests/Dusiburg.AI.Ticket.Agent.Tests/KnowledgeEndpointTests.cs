using System.Net;
using System.Net.Http.Json;
using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Core.Tests.Support;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.DbInit;
using Dusiburg.AI.Ticket.Rag.Indexing;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Knowledge;
using Dusiburg.AI.Ticket.Shared.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Dusiburg.AI.Ticket.Agent.Tests;

/// <summary>
/// <c>/api/knowledge</c> (Fase 9b) su un database di test con i dati di <c>data/</c> e l'indice costruito con un embedding finto:
/// ricerca, lettura di una fonte, note di rilascio, storico del cliente.
/// </summary>
[Category("RequiresSql2025")]
public class KnowledgeEndpointTests
{
    private const int Dimensions = 4;

    private string _connectionString = null!;
    private WebApplicationFactory<AgentEntryPoint> _factory = null!;
    private HttpClient _http = null!;

    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [OneTimeSetUp]
    public async Task CreateDatabaseAndHost()
    {
        _connectionString = TicketDatabaseInitializer.LocalConnectionStringFor($"Ticket_Test_{Guid.NewGuid():N}");
        await TicketDatabaseInitializer.RecreateAsync(_connectionString, Dimensions, seed: true, CancellationToken);

        await using (TicketDbContext db = TicketDatabaseInitializer.Create(_connectionString, Dimensions))
        {
            AiOptions options = AiOptions.FromConfiguration(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [AiOptions.Keys.EmbeddingDimensions] = "4" }).Build());
            await new IndexingPipeline(db, new HashingEmbeddingService(Dimensions), options, TimeProvider.System)
                .RunAsync(new EmbedRequest(OnlyMissing: false), null, CancellationToken);
        }

        _factory = new WebApplicationFactory<AgentEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:sql", _connectionString);
            builder.UseSetting(AiOptions.Keys.EmbeddingDimensions, "4");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IEmbeddingService>();
                services.AddSingleton<IEmbeddingService>(new HashingEmbeddingService(Dimensions));
            });
        });
        _http = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public async Task DropDatabase()
    {
        _http.Dispose();
        await _factory.DisposeAsync();
        await TicketDatabaseInitializer.DropAsync(_connectionString);
    }

    [Test]
    public async Task Search_ReturnsSourcesWithApplicabilityExcerptAndChunks()
    {
        //SUT
        KnowledgeSearchResult? result = await _http.GetFromJsonAsync<KnowledgeSearchResult>(
            "/api/knowledge/search?text=fatture%20in%20errore%20FF-E2041%20handshake%20TLS&product=FF&mode=OnPrem&version=5.1.2&max=5",
            TicketJson.Options, CancellationToken);

        Assert.That(result!.Sources, Has.Count.InRange(1, 5));
        Assert.That((result.ProductCode, result.Mode, result.Version), Is.EqualTo(("FF", (DeploymentMode?)DeploymentMode.OnPrem, "5.1.2")));
        Assert.That(result.Sources.Select(s => s.Applicability), Has.All.StartWith("FF · "));
        Assert.That(result.Sources.Select(s => s.Excerpt.Length), Has.All.LessThanOrEqualTo(Knowledge.KnowledgeEndpoints.ExcerptMaxChars + 1));
        Assert.That(result.Sources.Select(s => s.Chunks.Count), Has.All.GreaterThan(0));
    }

    [Test]
    public async Task Search_WithoutText_Is400()
    {
        //SUT
        using HttpResponseMessage response = await _http.GetAsync("/api/knowledge/search?product=FF", CancellationToken);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Source_ArticleKey_AllItsChunksInOrder_UnknownKeyIs404()
    {
        //SETUP
        KbArticle article = await FirstAsync(db => db.KbArticles.Where(a => db.Chunks.Any(c => c.KbArticleId == a.Id)).OrderBy(a => a.Code));
        string key = article.Code;

        //SUT
        KnowledgeDocument? document = await _http.GetFromJsonAsync<KnowledgeDocument>($"/api/knowledge/sources/{key}", TicketJson.Options, CancellationToken);
        using HttpResponseMessage missing = await _http.GetAsync("/api/knowledge/sources/KB-NOPE", CancellationToken);

        int chunks = await CountAsync(db => db.Chunks.Where(c => c.KbArticleId == article.Id));
        Assert.That((document!.Source, document.Key, document.Chunks.Count), Is.EqualTo((ChunkSource.KbArticle, key, chunks)));
        Assert.That(document.Chunks.Select(c => c.Section), Is.Ordered);
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task ReleaseNotes_BetweenTwoVersions_OnlyThoseVersionsInOrder()
    {
        //SUT
        List<ReleaseNoteItem>? notes = await _http.GetFromJsonAsync<List<ReleaseNoteItem>>(
            "/api/knowledge/release-notes?product=FF&from=5.1.2&to=5.2.0", TicketJson.Options, CancellationToken);
        using HttpResponseMessage unknown = await _http.GetAsync("/api/knowledge/release-notes?product=FF&from=9.9.9", CancellationToken);

        Assert.That(notes, Is.Not.Empty);
        Assert.That(notes!.Select(n => n.Version).Distinct(), Is.SubsetOf(new[] { "5.1.2", "5.2.0" }));
        Assert.That(notes.Select(n => n.Version), Is.Ordered.Using<string>((a, b) => string.CompareOrdinal(a, b)));
        Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await unknown.Content.ReadAsStringAsync(CancellationToken), Does.Contain("5.2.1"), "elenca le versioni ammesse");
    }

    [Test]
    public async Task CustomerTickets_SameCompanyWithoutTheTestSet()
    {
        //SETUP
        HistoricalTicket sample = await FirstAsync(db => db.HistoricalTickets.Where(t => !t.IsTestSet).OrderBy(t => t.Id));
        int expected = await CountAsync(db => db.HistoricalTickets.Where(t => t.CustomerId == sample.CustomerId && !t.IsTestSet));

        //SUT
        CustomerHistory? history = await _http.GetFromJsonAsync<CustomerHistory>(
            $"/api/knowledge/customer-tickets?email={Uri.EscapeDataString(sample.ContactEmail)}&max=20", TicketJson.Options, CancellationToken);

        List<string> testSet = await ListAsync(db => db.HistoricalTickets.Where(t => t.IsTestSet).Select(t => t.Number));
        Assert.That(history!.CustomerName, Is.Not.Null);
        Assert.That(history.Tickets, Has.Count.EqualTo(Math.Min(expected, 20)));
        Assert.That(history.Tickets.Select(t => t.Number), Has.None.AnyOf(testSet));
        Assert.That(history.Tickets.Select(t => t.OpenedAt), Is.Ordered.Descending);
    }

    private async Task<T> FirstAsync<T>(Func<TicketDbContext, IQueryable<T>> query)
    {
        await using TicketDbContext db = TicketDatabaseInitializer.Create(_connectionString, Dimensions);

        return await query(db).FirstAsync(CancellationToken);
    }

    private async Task<int> CountAsync<T>(Func<TicketDbContext, IQueryable<T>> query)
    {
        await using TicketDbContext db = TicketDatabaseInitializer.Create(_connectionString, Dimensions);

        return await query(db).CountAsync(CancellationToken);
    }

    private async Task<List<T>> ListAsync<T>(Func<TicketDbContext, IQueryable<T>> query)
    {
        await using TicketDbContext db = TicketDatabaseInitializer.Create(_connectionString, Dimensions);

        return await query(db).ToListAsync(CancellationToken);
    }
}
