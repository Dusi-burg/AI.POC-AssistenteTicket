using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.DbInit;
using Dusiburg.AI.Ticket.Shared.Demo;
using Dusiburg.AI.Ticket.Shared.Domain;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Data.Tests;

/// <summary>
/// <c>DbInit</c> su un database dedicato di <c>(localdb)\localdev</c> con <c>VECTOR(4)</c>: schemi, lookup, seed del catalogo,
/// riesecuzione e vincoli. La colonna <c>VECTOR</c> esiste solo da SQL Server 2025, che la CI può non avere.
/// </summary>
[Category("RequiresSql2025")]
public class DatabaseInitializerTests
{
    private const int Dimensions = 4;

    private string _connectionString = null!;

    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [OneTimeSetUp]
    public async Task CreateDatabase()
    {
        _connectionString = TicketDatabaseInitializer.LocalConnectionStringFor($"Ticket_Test_{Guid.NewGuid():N}");
        await TicketDatabaseInitializer.RecreateAsync(_connectionString, Dimensions, seed: true, CancellationToken);
    }

    [OneTimeTearDown]
    public async Task DropDatabase() => await TicketDatabaseInitializer.DropAsync(_connectionString);

    [Test]
    public async Task Recreate_Tables_LiveInTheThreeSchemas()
    {
        //SETUP
        await using TicketDbContext db = Open();

        //SUT
        List<string> tables = await db.Database
            .SqlQuery<string>($"SELECT TABLE_SCHEMA + '.' + TABLE_NAME AS [Value] FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'")
            .ToListAsync(CancellationToken);

        Assert.That(tables, Is.SupersetOf(new[]
        {
            "support.Product", "support.ProductVersion", "support.Customer", "support.HistoricalTicket", "support.TicketMessage",
            "support.Resolution", "support.KbArticle", "support.ReleaseNote",
            "support.DeploymentMode", "support.SlaLevel", "support.TicketPriority", "support.TicketState", "support.MessageAuthor",
            "support.ReleaseNoteType",
            "rag.Chunk", "rag.EmbeddingInfo", "rag.ChunkSource",
            "agent.AgentRun", "agent.AgentDraft", "agent.AgentDraftSource",
            "agent.RunTrigger", "agent.AgentRunState", "agent.DraftKind", "agent.DraftOutcome"
        }));
        Assert.That(tables, Has.None.StartsWith("dbo."));
    }

    [Test]
    public async Task Recreate_Lookups_HoldExactlyTheEnumMembers()
    {
        //SETUP
        await using TicketDbContext db = Open();

        //SUT
        string[] modes = await db.DeploymentModes.OrderBy(l => l.Id).Select(l => l.Name).ToArrayAsync(CancellationToken);
        string[] states = await db.TicketStates.OrderBy(l => l.Id).Select(l => l.Name).ToArrayAsync(CancellationToken);
        string[] sources = await db.ChunkSources.OrderBy(l => l.Id).Select(l => l.Name).ToArrayAsync(CancellationToken);
        string[] runStates = await db.AgentRunStates.OrderBy(l => l.Id).Select(l => l.Name).ToArrayAsync(CancellationToken);
        string[] outcomes = await db.DraftOutcomes.OrderBy(l => l.Id).Select(l => l.Name).ToArrayAsync(CancellationToken);

        Assert.That(modes, Is.EqualTo(Enum.GetNames<DeploymentMode>()));
        Assert.That(states, Is.EqualTo(Enum.GetNames<TicketState>()));
        Assert.That(sources, Is.EqualTo(Enum.GetNames<ChunkSource>()));
        Assert.That(runStates, Is.EqualTo(Enum.GetNames<AgentRunState>()));
        Assert.That(outcomes, Is.EqualTo(Enum.GetNames<DraftOutcome>()));
    }

    [Test]
    public async Task Recreate_WithSeed_WritesTheDemoCatalog()
    {
        //SETUP
        await using TicketDbContext db = Open();

        //SUT
        List<Product> products = await db.Products.Include(p => p.Versions).OrderBy(p => p.Id).ToListAsync(CancellationToken);
        List<Customer> customers = await db.Customers.ToListAsync(CancellationToken);
        int tickets = await db.HistoricalTickets.CountAsync(CancellationToken);
        int embeddingInfo = await db.EmbeddingInfo.CountAsync(CancellationToken);

        Assert.That(products.Select(p => p.Code), Is.EqualTo(DemoCatalog.Products.Select(p => p.Code)));
        Assert.That(
            products.Single(p => p.Code == DemoCatalog.FattureFlow).Versions.OrderBy(v => v.Sequence).Select(v => v.Version),
            Is.EqualTo(DemoCatalog.Product(DemoCatalog.FattureFlow).Versions.Select(v => v.Version)));
        Assert.That(customers.Select(c => c.EmailDomain), Is.EquivalentTo(DemoCatalog.Customers.Select(c => c.EmailDomain)));
        Assert.That(customers.Single(c => c.EmailDomain == "archivinotarili.example.it").ProductVersionId, Is.Null, "scenario 3: versione sconosciuta");
        Assert.That((tickets, embeddingInfo), Is.EqualTo((0, 0)), "storico dalla Fase 3, EmbeddingInfo da Cli embed");
    }

    [Test]
    public async Task Recreate_Embedding_HasTheConfiguredDimensions()
    {
        //SETUP
        await using TicketDbContext db = Open();

        //SUT
        int dimensions = await db.Database
            .SqlQuery<int>($"SELECT vector_dimensions AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'rag.Chunk') AND name = N'Embedding'")
            .SingleAsync(CancellationToken);

        Assert.That(dimensions, Is.EqualTo(Dimensions));
    }

    [Test]
    public async Task Recreate_RunTwice_GivesTheSameDatabase()
    {
        //SETUP
        string connectionString = TicketDatabaseInitializer.LocalConnectionStringFor($"Ticket_Test_{Guid.NewGuid():N}");

        try
        {
            //SUT
            await TicketDatabaseInitializer.RecreateAsync(connectionString, Dimensions, seed: true, CancellationToken);
            await TicketDatabaseInitializer.RecreateAsync(connectionString, Dimensions, seed: true, CancellationToken);

            await using TicketDbContext db = TicketDatabaseInitializer.Create(connectionString, Dimensions);
            Assert.That(await db.Customers.CountAsync(CancellationToken), Is.EqualTo(DemoCatalog.Customers.Count));
        }
        finally
        {
            await TicketDatabaseInitializer.DropAsync(connectionString);
        }
    }

    [Test]
    public async Task Chunk_WithVector_IsSavedAndReadBack()
    {
        //SETUP
        await using TicketDbContext db = Open();
        KbArticle article = await AddArticleAsync(db, "KB-TEST-1");
        float[] vector = [0.1f, 0.2f, 0.3f, 0.4f];
        db.Chunks.Add(new Chunk
        {
            Source = ChunkSource.KbArticle, KbArticleId = article.Id, Section = 1, ProductId = article.ProductId,
            Text = "Testo di prova", Embedding = new SqlVector<float>(vector)
        });

        //SUT
        await db.SaveChangesAsync(CancellationToken);

        db.ChangeTracker.Clear();
        Chunk saved = await db.Chunks.SingleAsync(c => c.KbArticleId == article.Id, CancellationToken);

        Assert.That(saved.Embedding?.Memory.ToArray(), Is.EqualTo(vector));
    }

    [Test]
    public async Task Chunk_SourceDifferentFromItsForeignKey_IsRejectedByTheDatabase()
    {
        //SETUP
        await using TicketDbContext db = Open();
        KbArticle article = await AddArticleAsync(db, "KB-TEST-2");
        db.Chunks.Add(new Chunk
        {
            Source = ChunkSource.HistoricalTicket, KbArticleId = article.Id, Section = 1, ProductId = article.ProductId, Text = "Fonte incoerente"
        });

        //SUT
        Assert.That(async () => await db.SaveChangesAsync(CancellationToken), Throws.TypeOf<DbUpdateException>());
    }

    [Test]
    public async Task AgentRun_SameZammadArticleTwice_IsRejectedByTheDatabase()
    {
        //SETUP
        await using TicketDbContext db = Open();
        db.AgentRuns.Add(NewRun(zammadTicketId: 10, zammadArticleId: 20));
        await db.SaveChangesAsync(CancellationToken);
        db.AgentRuns.Add(NewRun(zammadTicketId: 10, zammadArticleId: 20));

        //SUT
        Assert.That(async () => await db.SaveChangesAsync(CancellationToken), Throws.TypeOf<DbUpdateException>());
    }

    [Test]
    public async Task AgentRun_WithDraftAndSources_KeepsItsGraph()
    {
        //SETUP
        await using TicketDbContext db = Open();
        AgentRun run = NewRun(zammadTicketId: 11, zammadArticleId: 21);
        run.Draft = new AgentDraft
        {
            Kind = DraftKind.Clarification, Language = "it", Body = "Gentile cliente, ci servono alcune informazioni.",
            ChatModel = "qwen3.5:9b", PromptHash = new string('a', 64), Confidence = 0.4, CreatedAt = DateTimeOffset.UtcNow,
            MissingInformation = ["versione installata"], Questions = ["Quale versione di DocuArchive usa?"],
            Sources = { new AgentDraftSource { Rank = 1, Source = ChunkSource.KbArticle, SourceKey = "KB-DA-0001", IsCitedByModel = true } }
        };
        db.AgentRuns.Add(run);

        //SUT
        await db.SaveChangesAsync(CancellationToken);

        db.ChangeTracker.Clear();
        AgentRun saved = await db.AgentRuns.Include(r => r.Draft!).ThenInclude(d => d.Sources).SingleAsync(r => r.Id == run.Id, CancellationToken);

        Assert.That(saved.Draft!.Outcome, Is.EqualTo(DraftOutcome.Unknown));
        Assert.That(saved.Draft.Questions, Is.EqualTo(new[] { "Quale versione di DocuArchive usa?" }));
        Assert.That(saved.Draft.MissingInformation, Is.EqualTo(new[] { "versione installata" }));
        Assert.That(saved.Draft.Sources.Single().SourceKey, Is.EqualTo("KB-DA-0001"));
    }

    private TicketDbContext Open() => TicketDatabaseInitializer.Create(_connectionString, Dimensions);

    private static async Task<KbArticle> AddArticleAsync(TicketDbContext db, string code)
    {
        short productId = await db.Products.Where(p => p.Code == DemoCatalog.DocuArchive).Select(p => p.Id).SingleAsync(CancellationToken);
        var article = new KbArticle { Code = code, ProductId = productId, Title = "Articolo di prova", Body = "Corpo", UpdatedOn = new DateOnly(2026, 10, 1) };

        db.KbArticles.Add(article);
        await db.SaveChangesAsync(CancellationToken);

        return article;
    }

    private static AgentRun NewRun(int zammadTicketId, int zammadArticleId) => new()
    {
        ZammadTicketId = zammadTicketId, ZammadArticleId = zammadArticleId, Trigger = RunTrigger.Webhook, State = AgentRunState.Queued,
        ReceivedAt = DateTimeOffset.UtcNow
    };
}
