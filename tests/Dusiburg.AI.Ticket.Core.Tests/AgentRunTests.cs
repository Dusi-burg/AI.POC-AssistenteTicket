using System.Text.Json;
using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Core.Composition;
using Dusiburg.AI.Ticket.Core.Context;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Core.Feedback;
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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dusiburg.AI.Ticket.Core.Tests;

/// <summary>
/// La pipeline per intero con le elaborazioni registrate: database dedicato di <c>(localdb)\localdev</c> con <c>VECTOR(4)</c>, indice
/// costruito da <c>data/</c> con un embedding finto, modello finto e Zammad finto (G12).
/// </summary>
[Category("RequiresSql2025")]
public class AgentRunTests
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
    public async Task Process_ClearTicket_PublishesTheDraftAndRegistersRunDraftAndSources()
    {
        //SETUP
        await using TicketDbContext db = Open();
        FakeZammad zammad = Zammad(ticketId: 170, articleId: 429);
        // Il modello può citare solo le fonti del prompt (schema): si legge quella vera dalla prima chiamata.
        var factory = new CitingFirstSourceFactory(Classification);
        AgentRunService service = Service(db, zammad, factory);
        int runId = (await service.TryCreateAsync(170, 429, RunTrigger.Webhook, CancellationToken))!.Value;

        //SUT
        AgentRun run = await service.ProcessAsync(runId, trace => $"http://dashboard/traces/detail/{trace}", CancellationToken);

        AgentDraft draft = await db.AgentDrafts.AsNoTracking().Include(d => d.Sources).SingleAsync(d => d.AgentRunId == runId, CancellationToken);
        Assert.That(run.State, Is.EqualTo(AgentRunState.Completed), run.Error);
        Assert.That((run.TraceId, run.DurationMs), Is.Not.EqualTo(((string?)null, (int?)null)));
        Assert.That(draft.Body, Does.EndWith(DraftComposer.AiDisclosure));
        Assert.That(draft.Sources, Has.Count.EqualTo(RetrievalOptions.DefaultPromptSourcesMax));
        Assert.That(draft.Sources.Count(s => s.IsCitedByModel && s.IsVerified), Is.EqualTo(1));
        Assert.That(draft.PromptHash, Has.Length.EqualTo(64));

        string sharedDraft = zammad.Writes.Single(w => w.Path == "/api/v1/tickets/170/shared_draft").Body!;
        Assert.That(sharedDraft, Does.Contain(DraftComposer.AiDisclosure));
        Assert.That(zammad.Writes.Count(w => w.Path == "/api/v1/ticket_articles"), Is.EqualTo(1));
    }

    [Test]
    public async Task TryCreate_SameArticleTwice_TheSecondDeliveryIsDiscarded()
    {
        //SETUP
        await using TicketDbContext db = Open();
        AgentRunService service = Service(db, new FakeZammad(), ScriptedChatClientFactory.With([], []));

        //SUT
        int? first = await service.TryCreateAsync(500, 900, RunTrigger.Webhook, CancellationToken);
        int? second = await service.TryCreateAsync(500, 900, RunTrigger.Polling, CancellationToken);
        int? otherArticle = await service.TryCreateAsync(500, 901, RunTrigger.Webhook, CancellationToken);

        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.Null);
        Assert.That(otherArticle, Is.Not.Null.And.Not.EqualTo(first));
        Assert.That(await service.PendingAsync(CancellationToken), Does.Contain(first!.Value).And.Contain(otherArticle!.Value));
    }

    [Test]
    public async Task Process_ModelAnswersInvalidJsonTwice_FailedWithCodeAndNothingWrittenToZammad()
    {
        //SETUP
        await using TicketDbContext db = Open();
        FakeZammad zammad = Zammad(ticketId: 171, articleId: 440);
        AgentRunService service = Service(db, zammad, ScriptedChatClientFactory.With([Classification], ["{", "no"]));
        int runId = (await service.TryCreateAsync(171, 440, RunTrigger.Manual, CancellationToken))!.Value;

        //SUT
        AgentRun run = await service.ProcessAsync(runId, null, CancellationToken);

        Assert.That((run.State, run.ErrorCode), Is.EqualTo((AgentRunState.Failed, InvalidModelOutputException.Code)));
        Assert.That(await db.AgentDrafts.AnyAsync(d => d.AgentRunId == runId, CancellationToken), Is.False);
        Assert.That(zammad.Writes, Is.Empty);
    }

    [Test]
    public async Task Process_ModelCannotAnswerWithEmptyText_PublishesFixedTextNotAnEmptyAnswer()
    {
        //SETUP
        await using TicketDbContext db = Open();
        FakeZammad zammad = Zammad(ticketId: 172, articleId: 450);
        const string EmptyDraft = """{"puoRispondere":false,"autovalutazione":0,"risposta":"","fonti":[],"domandePerIlCliente":[]}""";
        AgentRunService service = Service(db, zammad, ScriptedChatClientFactory.With([Classification], [EmptyDraft]));
        int runId = (await service.TryCreateAsync(172, 450, RunTrigger.Manual, CancellationToken))!.Value;

        //SUT
        AgentRun run = await service.ProcessAsync(runId, null, CancellationToken);

        AgentDraft draft = await db.AgentDrafts.AsNoTracking().SingleAsync(d => d.AgentRunId == runId, CancellationToken);
        Assert.That(run.State, Is.EqualTo(AgentRunState.Completed), run.Error);
        Assert.That(draft.Kind, Is.Not.EqualTo(DraftKind.Answer));
        Assert.That(draft.Body, Does.StartWith("Gentile Giorgio,").And.EndWith(DraftComposer.AiDisclosure));
    }

    [Test]
    public async Task CheckOutcome_DraftSentAsPublished_SentUnchangedTaggedAndRegisteredOnce()
    {
        //SETUP
        await using TicketDbContext db = Open();
        FakeZammad zammad = Zammad(ticketId: 173, articleId: 460);
        AgentRunService service = Service(db, zammad, new CitingFirstSourceFactory(Classification));
        int runId = (await service.TryCreateAsync(173, 460, RunTrigger.Webhook, CancellationToken))!.Value;
        await service.ProcessAsync(runId, null, CancellationToken);
        ReplyWith(zammad, ticketId: 173, customerArticleId: 460, SharedDraftBody(zammad, 173));
        DraftOutcomeService outcomes = Outcomes(db, zammad);

        //SUT
        IReadOnlyList<ResolvedOutcome> first = await outcomes.CheckTicketAsync(173, CancellationToken);
        IReadOnlyList<ResolvedOutcome> second = await outcomes.CheckTicketAsync(173, CancellationToken);

        AgentRun run = await db.AgentRuns.AsNoTracking().Include(r => r.Draft).SingleAsync(r => r.Id == runId, CancellationToken);
        AgentDraft draft = run.Draft!;
        Assert.That(first.Select(r => (r.RunId, r.Outcome)), Is.EqualTo(new[] { (runId, DraftOutcome.SentUnchanged) }));
        Assert.That(first.Single().Tags, Is.EqualTo(new[] { ZammadPublisher.GeneratedTag }));
        Assert.That(second, Is.Empty, "un secondo evento non cambia nulla");
        Assert.That((draft.Outcome, draft.ReplyArticleId, draft.EditDistance, draft.DisclosureKept), Is.EqualTo((DraftOutcome.SentUnchanged, (int?)1000, (double?)0, (bool?)true)));
        Assert.That(draft.FinalText, Does.Contain("verifichi il certificato").And.Contain(DraftComposer.AiDisclosure));
        Assert.That((draft.NoteArticleId, run.TicketNumber, run.TicketTitle), Is.EqualTo((999, "7173", "Fatture non trasmesse: errore FF-E2041")));
        Assert.That(draft.GenerationMs + draft.PublicationMs, Is.GreaterThanOrEqualTo(0));
        Assert.That(zammad.Writes.Count(w => w.Path == "/api/v1/tags/add" && w.Body!.Contains("\"ai-generata\"")), Is.EqualTo(1));
    }

    [Test]
    public async Task CheckOutcome_EditedWithoutDisclosure_SentEditedWithTheRemovalTagAndNote()
    {
        //SETUP
        await using TicketDbContext db = Open();
        FakeZammad zammad = Zammad(ticketId: 174, articleId: 470);
        AgentRunService service = Service(db, zammad, new CitingFirstSourceFactory(Classification));
        int runId = (await service.TryCreateAsync(174, 470, RunTrigger.Webhook, CancellationToken))!.Value;
        await service.ProcessAsync(runId, null, CancellationToken);
        string published = SharedDraftBody(zammad, 174);
        string sent = published[..published.IndexOf("<p><em>", StringComparison.Ordinal)].Replace("<p>Cordiali saluti,</p>", "<p>Resto a disposizione.</p><p>Cordiali saluti,</p>");
        ReplyWith(zammad, ticketId: 174, customerArticleId: 470, sent);
        int notesBefore = zammad.Writes.Count(w => w.Path == "/api/v1/ticket_articles");

        //SUT
        ResolvedOutcome resolved = (await Outcomes(db, zammad).CheckTicketAsync(174, CancellationToken)).Single();

        Assert.That(resolved.Outcome, Is.EqualTo(DraftOutcome.SentEdited));
        Assert.That(resolved.EditDistance, Is.GreaterThan(0).And.LessThanOrEqualTo(AgentOptions.DefaultFeedbackMaxEditDistance));
        Assert.That(resolved.Tags, Is.EqualTo(new[] { ZammadPublisher.PartlyGeneratedTag, ZammadPublisher.DisclosureRemovedTag }));
        Assert.That(resolved.DisclosureKept, Is.False);
        Assert.That(zammad.Writes.Count(w => w.Path == "/api/v1/ticket_articles") - notesBefore, Is.EqualTo(1), "nota interna sulla dicitura tolta");
    }

    private DraftOutcomeService Outcomes(TicketDbContext db, FakeZammad zammad) =>
        new(db, zammad.Client(), new ZammadPublisher(zammad.Client()), AgentOptions.Default, TimeProvider.System, NullLogger<DraftOutcomeService>.Instance);

    /// <summary>L'HTML della bozza condivisa pubblicata dall'elaborazione: quello che l'operatore trova nell'editor.</summary>
    private static string SharedDraftBody(FakeZammad zammad, long ticketId)
    {
        using JsonDocument shared = JsonDocument.Parse(zammad.Writes.Last(w => w.Path == $"/api/v1/tickets/{ticketId}/shared_draft").Body!);

        return shared.RootElement.GetProperty("new_article").GetProperty("body").GetString()!;
    }

    /// <summary>Gli articoli dopo l'invio: messaggio del cliente, nota dell'assistente (999), risposta dell'operatore (1000).</summary>
    private static void ReplyWith(FakeZammad zammad, long ticketId, long customerArticleId, string replyHtml) =>
        zammad.On("GET", $"/api/v1/ticket_articles/by_ticket/{ticketId}", $$"""
            [
              {"id":{{customerArticleId}},"ticket_id":{{ticketId}},"sender":"Customer","type":"web","internal":false,"body":"Errore FF-E2041.","content_type":"text/plain","created_at":"2026-10-19T09:00:00Z"},
              {"id":999,"ticket_id":{{ticketId}},"sender":"Agent","type":"note","internal":true,"body":"<p>nota</p>","content_type":"text/html","created_at":"2026-10-19T09:01:00Z"},
              {"id":1000,"ticket_id":{{ticketId}},"sender":"Agent","type":"note","internal":false,"body":{{JsonSerializer.Serialize(replyHtml)}},"content_type":"text/html","created_at":"2026-10-19T09:05:00Z"}
            ]
            """);

    private TicketDbContext Open() => TicketDatabaseInitializer.Create(_connectionString, Dimensions);

    private static AgentRunService Service(TicketDbContext db, FakeZammad zammad, IChatClientFactory factory)
    {
        var client = zammad.Client();
        var pipeline = new TicketPipeline(
            new TicketContextLoader(client, db),
            new TicketLanguageModel(factory),
            new HybridRetriever(db, new HashingEmbeddingService(Dimensions), new ChunkCatalogCache(), RetrievalOptions.Default),
            new ZammadPublisher(client),
            db,
            AgentOptions.Default,
            new ZammadOptions(new Uri("http://zammad.test:8080/"), "token"));

        return new AgentRunService(db, pipeline, TimeProvider.System, NullLogger<AgentRunService>.Instance);
    }

    /// <summary>Un ticket dello scenario 1 (FF on-premise 5.1.2) con il suo cliente, e le scritture della pubblicazione.</summary>
    private static FakeZammad Zammad(long ticketId, long articleId) => new FakeZammad()
        .On("GET", $"/api/v1/tickets/{ticketId}", $$"""{"id":{{ticketId}},"number":"7{{ticketId}}","title":"Fatture non trasmesse: errore FF-E2041","group_id":2,"customer_id":59,"organization_id":7,"created_at":"2026-10-16T09:00:00Z","updated_at":"2026-10-16T09:00:00Z"}""")
        .On("GET", $"/api/v1/ticket_articles/by_ticket/{ticketId}", $$"""[{"id":{{articleId}},"ticket_id":{{ticketId}},"sender":"Customer","type":"web","internal":false,"body":"Da stamattina tutte le fatture vanno in errore FF-E2041, handshake TLS rifiutato.","content_type":"text/plain","created_at":"2026-10-16T09:00:00Z"}]""")
        .On("GET", "/api/v1/users/59", """{"id":59,"login":"g","email":"giorgio.ferretti@ferretti-contabilita.example.it","firstname":"Giorgio","lastname":"Ferretti"}""")
        .On("GET", "/api/v1/organizations/7", """{"id":7,"name":"Studio Contabile Ferretti S.r.l.","prodotto":"FF","versione_prodotto":"5.1.2","modalita":"onprem","livello_sla":"plus"}""")
        .On("PUT", $"/api/v1/tickets/{ticketId}/shared_draft", "{}")
        .On("POST", "/api/v1/ticket_articles", $$"""{"id":999,"ticket_id":{{ticketId}},"internal":true,"body":"x","created_at":"2026-10-16T10:00:00Z"}""")
        .On("POST", "/api/v1/tags/add", "true");

    /// <summary>Modello finto che cita la prima fonte ammessa dallo schema della bozza, come farebbe Ollama vincolato dallo schema.</summary>
    private sealed class CitingFirstSourceFactory(string classification) : IChatClientFactory
    {
        public ChatModel Create(ModelTask task) => new(new CitingClient(task, classification), task, "stub", "stub", new ChatOptions());

        private sealed class CitingClient(ModelTask task, string classification) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                string reply = task == ModelTask.Classify
                    ? classification
                    : $$"""{"puoRispondere":true,"autovalutazione":0.9,"risposta":"Gentile Giorgio,\n\nverifichi il certificato.\n\nCordiali saluti,","fonti":[{"id":"{{FirstAllowedId(options)}}","motivo":"stesso errore"}],"domandePerIlCliente":[]}""";

                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
            }

            private static string FirstAllowedId(ChatOptions? options) =>
                ((ChatResponseFormatJson)options!.ResponseFormat!).Schema!.Value
                    .GetProperty("properties").GetProperty("fonti").GetProperty("items").GetProperty("properties").GetProperty("id").GetProperty("enum")[0].GetString()!;

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}
