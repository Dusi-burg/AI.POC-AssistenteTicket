using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Core.Context;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Core.Pipeline;
using Dusiburg.AI.Ticket.Core.Publishing;
using Dusiburg.AI.Ticket.Core.Tests.Support;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.DbInit;
using Dusiburg.AI.Ticket.Rag.Indexing;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Zammad;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dusiburg.AI.Ticket.Core.Tests;

/// <summary>
/// La modalità agente (Fase 9b) nella pipeline: un agente finto restituisce bozza, fonti lette e chiamate; verifica, confidenza,
/// pubblicazione e registrazione sono quelle della pipeline fissa, sulle sole fonti lette. Database come <see cref="AgentRunTests"/>.
/// </summary>
[Category("RequiresSql2025")]
public class AgentModeTests
{
    private const int Dimensions = 4;

    private const string Classification = """{"categoria":"trasmissione","priorita":"alta","lingua":"it","informazioniMancanti":[],"sospettaManipolazione":false}""";

    private string _connectionString = null!;

    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [OneTimeSetUp]
    public async Task CreateAndIndexDatabase()
    {
        _connectionString = TicketDatabaseInitializer.LocalConnectionStringFor($"Ticket_Test_{Guid.NewGuid():N}");
        await TicketDatabaseInitializer.RecreateAsync(_connectionString, Dimensions, seed: true, CancellationToken);

        await using TicketDbContext db = Open();
        AiOptions options = AiOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AiOptions.Keys.EmbeddingDimensions] = "4" }).Build());
        await new IndexingPipeline(db, new HashingEmbeddingService(Dimensions), options, TimeProvider.System)
            .RunAsync(new EmbedRequest(OnlyMissing: false), null, CancellationToken);
    }

    [OneTimeTearDown]
    public async Task DropDatabase() => await TicketDatabaseInitializer.DropAsync(_connectionString);

    [Test]
    public async Task Process_AgentMode_VerifiesOnlyTheSourcesReadAndRecordsTheToolCalls()
    {
        //SETUP
        await using TicketDbContext db = Open();
        IndexedChunk read = (await Retriever(db).CatalogAsync(CancellationToken)).Chunks.First(c => c.Source == ChunkSource.HistoricalTicket);
        var agent = new FakeToolAgent(
            Draft(canAnswer: true, read.SourceKey, "KB-FF-9999"),
            [new SourceEvidence(ChunkSource.HistoricalTicket, read.SourceKey, 0.03, [new EvidenceChunk(read.Id, 0.15)])],
            [new ToolCallRecord("cerca_fonti", """{"testo":"FF-E2041"}""", "ok", TimeSpan.FromMilliseconds(40), null),
             new ToolCallRecord("leggi_fonte", """{"chiave":"HT-0000"}""", "rejected:NOT_ALLOWED", TimeSpan.Zero, "non uscita da una ricerca")]);
        FakeZammad zammad = Zammad(ticketId: 180, articleId: 460);
        AgentRunService service = Service(db, zammad, agent);
        int runId = (await service.TryCreateAsync(180, 460, RunTrigger.Webhook, CancellationToken))!.Value;

        //SUT
        AgentRun run = await service.ProcessAsync(runId, null, CancellationToken);

        AgentRun saved = await db.AgentRuns.AsNoTracking().Include(r => r.ToolCalls).Include(r => r.Draft).ThenInclude(d => d!.Sources)
            .SingleAsync(r => r.Id == runId, CancellationToken);
        Assert.That(run.State, Is.EqualTo(AgentRunState.Completed), run.Error);
        Assert.That(saved.Mode, Is.EqualTo(PipelineMode.Agent));
        Assert.That(saved.ToolCalls.OrderBy(c => c.Sequence).Select(c => (c.ToolName, c.Outcome)),
            Is.EqualTo(new[] { ("cerca_fonti", "ok"), ("leggi_fonte", "rejected:NOT_ALLOWED") }));
        Assert.That(saved.Draft!.Sources.Select(s => s.SourceKey), Is.EqualTo(new[] { read.SourceKey }), "solo le fonti lette");
        Assert.That(saved.Draft.Sources.Single().IsVerified, Is.True);
        Assert.That(agent.Tickets.Single().Number, Is.EqualTo("7180"));

        string note = zammad.Writes.Single(w => w.Path == "/api/v1/ticket_articles").Body!;
        Assert.That(note, Does.Contain("2 chiamate ai tool").And.Contain("rejected:NOT_ALLOWED").And.Contain("citazione di una fonte non fornita: KB-FF-9999"));
    }

    [Test]
    public async Task Process_AgentWithoutToolCalls_NoSourcesSoNoAnswer()
    {
        //SETUP
        await using TicketDbContext db = Open();
        var agent = new FakeToolAgent(Draft(canAnswer: true), [], []);
        FakeZammad zammad = Zammad(ticketId: 181, articleId: 461);
        AgentRunService service = Service(db, zammad, agent);
        int runId = (await service.TryCreateAsync(181, 461, RunTrigger.Webhook, CancellationToken))!.Value;

        //SUT
        await service.ProcessAsync(runId, null, CancellationToken);

        AgentDraft draft = await db.AgentDrafts.AsNoTracking().SingleAsync(d => d.AgentRunId == runId, CancellationToken);
        Assert.That(draft.Kind, Is.Not.EqualTo(DraftKind.Answer));
        Assert.That((draft.RetrievalScore, draft.CitationScore), Is.EqualTo((0.0, 0.0)));
        Assert.That(zammad.Writes.Single(w => w.Path == "/api/v1/ticket_articles").Body, Does.Contain("nessuna chiamata ai tool"));
    }

    [Test]
    public void Propose_AgentModeWithoutTheAgentRegistered_IsAConfigurationError()
    {
        //SETUP
        using TicketDbContext db = Open();
        TicketPipeline pipeline = Pipeline(db, Zammad(ticketId: 182, articleId: 462), null);

        //SUT
        Assert.That(async () => await pipeline.ProposeAsync(182, 462, CancellationToken),
            Throws.InvalidOperationException.With.Message.Contains("PIPELINE_MODE=agent"));
    }

    [Test]
    public async Task ToRetrievedSources_WholeSourceKeepsTheKnownDistanceAndAddsTheOtherChunks()
    {
        //SETUP
        await using TicketDbContext db = Open();
        ChunkCatalog catalog = await Retriever(db).CatalogAsync(CancellationToken);
        IGrouping<string, IndexedChunk> article = catalog.Chunks.Where(c => c.Source == ChunkSource.KbArticle)
            .GroupBy(c => c.SourceKey).First(g => g.Count() > 1);
        IndexedChunk seen = article.Last();

        //SUT
        IReadOnlyList<RetrievedSource> sources = new List<SourceEvidence>
        {
            new(ChunkSource.KbArticle, article.Key, 0.02, [new EvidenceChunk(seen.Id, 0.21)], Whole: true),
            new(ChunkSource.HistoricalTicket, "HT-INESISTENTE", 0.01, [new EvidenceChunk(-1, 0.1)])
        }.ToRetrievedSources(catalog);

        RetrievedSource source = sources.Single();
        Assert.That(source.Chunks.Select(c => c.Chunk.Id), Is.EquivalentTo(article.Select(c => c.Id)));
        Assert.That((source.Best.Chunk.Id, source.Best.VectorDistance), Is.EqualTo((seen.Id, (double?)0.21)));
        Assert.That(source.Chunks.Skip(1).Select(c => c.VectorDistance), Has.All.Null);
    }

    private static string Draft(bool canAnswer, params string[] cited) =>
        $$"""{"puoRispondere":{{(canAnswer ? "true" : "false")}},"autovalutazione":0.9,"risposta":"Gentile Giorgio,\n\nverifichi il certificato.\n\nCordiali saluti,","fonti":[{{string.Join(",", cited.Select(id => $$"""{"id":"{{id}}","motivo":"stesso errore"}"""))}}],"domandePerIlCliente":[]}""";

    private TicketDbContext Open() => TicketDatabaseInitializer.Create(_connectionString, Dimensions);

    private static HybridRetriever Retriever(TicketDbContext db) =>
        new(db, new HashingEmbeddingService(Dimensions), new ChunkCatalogCache(), RetrievalOptions.Default);

    private static AgentRunService Service(TicketDbContext db, FakeZammad zammad, IToolAgentDrafter agent) =>
        new(db, Pipeline(db, zammad, agent), TimeProvider.System, NullLogger<AgentRunService>.Instance);

    private static TicketPipeline Pipeline(TicketDbContext db, FakeZammad zammad, IToolAgentDrafter? agent)
    {
        var client = zammad.Client();

        return new TicketPipeline(
            new TicketContextLoader(client, db),
            new TicketLanguageModel(ScriptedChatClientFactory.With([Classification], [])),
            Retriever(db),
            new ZammadPublisher(client),
            db,
            AgentOptions.Default with { Mode = PipelineMode.Agent },
            new ZammadOptions(new Uri("http://zammad.test:8080/"), "token"),
            agent);
    }

    private static FakeZammad Zammad(long ticketId, long articleId) => new FakeZammad()
        .On("GET", $"/api/v1/tickets/{ticketId}", $$"""{"id":{{ticketId}},"number":"7{{ticketId}}","title":"Fatture non trasmesse: errore FF-E2041","group_id":2,"customer_id":59,"organization_id":7,"created_at":"2026-10-16T09:00:00Z","updated_at":"2026-10-16T09:00:00Z"}""")
        .On("GET", $"/api/v1/ticket_articles/by_ticket/{ticketId}", $$"""[{"id":{{articleId}},"ticket_id":{{ticketId}},"sender":"Customer","type":"web","internal":false,"body":"Da stamattina tutte le fatture vanno in errore FF-E2041, handshake TLS rifiutato.","content_type":"text/plain","created_at":"2026-10-16T09:00:00Z"}]""")
        .On("GET", "/api/v1/users/59", """{"id":59,"login":"g","email":"giorgio.ferretti@ferretti-contabilita.example.it","firstname":"Giorgio","lastname":"Ferretti"}""")
        .On("GET", "/api/v1/organizations/7", """{"id":7,"name":"Studio Contabile Ferretti S.r.l.","prodotto":"FF","versione_prodotto":"5.1.2","modalita":"onprem","livello_sla":"plus"}""")
        .On("PUT", $"/api/v1/tickets/{ticketId}/shared_draft", "{}")
        .On("POST", "/api/v1/ticket_articles", $$"""{"id":999,"ticket_id":{{ticketId}},"internal":true,"body":"x","created_at":"2026-10-16T10:00:00Z"}""")
        .On("POST", "/api/v1/tags/add", "true");

    /// <summary>Agente finto: bozza, fonti lette e chiamate preparate; registra i ticket ricevuti.</summary>
    private sealed class FakeToolAgent(string draft, IReadOnlyList<SourceEvidence> evidence, IReadOnlyList<ToolCallRecord> calls) : IToolAgentDrafter
    {
        public List<IncomingTicket> Tickets { get; } = [];

        public Task<ToolAgentDraft> DraftAsync(IncomingTicket ticket, ModelClassification classification, CancellationToken cancellationToken)
        {
            Tickets.Add(ticket);

            return Task.FromResult(new ToolAgentDraft(
                new ModelCall<ModelDraft>(ModelJson.Parse<ModelDraft>(draft).Value!, "stub-agent", new string('0', 64), 1), evidence, calls));
        }

        public IToolAgentDrafter WithModels(IChatClientFactory factory) => this;
    }
}
