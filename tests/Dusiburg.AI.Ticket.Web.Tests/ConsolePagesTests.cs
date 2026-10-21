using System.Net;
using System.Text;
using System.Text.Json;
using Dusiburg.AI.Ticket.Shared.Catalog;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Runs;
using Dusiburg.AI.Ticket.Shared.Serialization;
using Dusiburg.AI.Ticket.Web.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Web.Tests;

/// <summary>Pagine Elaborazioni, dettaglio, Esiti e Dati demo (Fase 7) con un Agent finto che risponde per percorso.</summary>
public class ConsolePagesTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);

    private static readonly RunSummary Run = new(
        12, 173, 460, "71173", "Fatture non trasmesse", RunTrigger.Webhook, AgentRunState.Completed, At, 11_800, null, null,
        "0123456789abcdef0123456789abcdef", DraftKind.Answer, 0.99, DraftOutcome.SentEdited);

    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [Test]
    public async Task GetRuns_ListWithLinksAndTheFiltersPassedToTheAgent()
    {
        //SETUP
        var agent = new RoutingAgentHandler().On("/api/runs", new[] { Run });
        await using WebApplicationFactory<WebEntryPoint> factory = Factory(agent);
        using HttpClient client = factory.CreateClient();

        //SUT
        string page = await client.GetStringAsync("/Runs?Kind=Answer&Outcome=SentEdited", CancellationToken);

        Assert.That(page, Does.Contain("id=\"runs\"").And.Contain("71173").And.Contain("Inviata modificata").And.Contain("0,99"));
        Assert.That(page, Does.Contain("href=\"http://zammad.test:8080/#ticket/zoom/173\""));
        Assert.That(page, Does.Contain("href=\"http://dashboard.test/traces/detail/0123456789abcdef0123456789abcdef\""));
        Assert.That(agent.Paths, Does.Contain("/api/runs?kind=Answer&outcome=SentEdited"));
    }

    [Test]
    public async Task GetRunDetails_SignalsTimingsSourcesAndDraftBesideTheSentText()
    {
        //SETUP
        var draft = new RunDraftView(
            DraftKind.Answer, "trasmissione", TicketPriority.High, [], true, 0.95, 1, 1, 1, 0.99, "Gentile Giorgio,\n\nbozza.", [], "qwen3.5:9b",
            new RunTimings(100, 1500, 1000, 8000, 10, 300),
            new RunOutcomeView(DraftOutcome.SentEdited, At, "inviata con modifiche", 1000, "Gentile Giorgio,\n\nbozza modificata.", 0.12, false),
            [new RunSourceView(1, ChunkSource.KbArticle, "KB-FF-0004", true, true, "stesso errore")]);
        var agent = new RoutingAgentHandler().On("/api/runs/12", new RunDetail(Run, draft));
        await using WebApplicationFactory<WebEntryPoint> factory = Factory(agent);
        using HttpClient client = factory.CreateClient();

        //SUT
        string page = await client.GetStringAsync("/Runs/Details/12", CancellationToken);
        using HttpResponseMessage missing = await client.GetAsync("/Runs/Details/99", CancellationToken);

        Assert.That(page, Does.Contain("id=\"timings\"").And.Contain("generazione").And.Contain("10,9 s"));
        Assert.That(page, Does.Contain("KB-FF-0004").And.Contain("stesso errore"));
        Assert.That(page, Does.Contain("id=\"draft-body\"").And.Contain("id=\"final-text\"").And.Contain("bozza modificata."));
        Assert.That(page, Does.Contain("ai-generata-in-parte").And.Contain("id=\"disclosure-removed\""));
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task GetOutcomes_UsefulShareCountsAndDisclosureWarnings()
    {
        //SETUP
        var summary = new OutcomeSummary(
            Drafts: 6, Pending: 1,
            Counts: [new(DraftKind.Answer, DraftOutcome.SentUnchanged, 2), new(DraftKind.Answer, DraftOutcome.SentEdited, 1), new(DraftKind.Clarification, DraftOutcome.Discarded, 2), new(DraftKind.Escalation, DraftOutcome.Unknown, 1)],
            AverageEditDistance: 0.04, Useful: 3, Decided: 5, UsefulMaxEditDistance: 0.3, FeedbackMaxEditDistance: 0.7,
            DisclosureRemovals: [new DisclosureRemoval(12, 173, "71173", 1000, At, DraftOutcome.SentEdited)]);
        await using WebApplicationFactory<WebEntryPoint> factory = Factory(new RoutingAgentHandler().On("/api/outcomes", summary));
        using HttpClient client = factory.CreateClient();

        //SUT
        string page = await client.GetStringAsync("/Outcomes", CancellationToken);

        Assert.That(page, Does.Contain("id=\"useful\">60%"));
        Assert.That(page, Does.Contain("id=\"disclosure-removals\"").And.Contain("71173").And.Contain("elaborazione 12"));
        Assert.That(page, Does.Contain("href=\"/Runs?kind=Clarification&amp;outcome=Discarded\""));
    }

    [Test]
    public async Task GetData_ProductsCustomersKnowledgeBaseAndReleaseNotes()
    {
        //SETUP
        var catalog = new CatalogView(
            [new ProductView("FF", "FattureFlow", [new VersionView("5.2.0", new DateOnly(2026, 3, 1), null)])],
            [new CustomerView("Studio Contabile Ferretti S.r.l.", "ferretti.example.it", "FF", "5.1.2", DeploymentMode.OnPrem, SlaLevel.Plus, true)],
            [new KbArticleView("KB-FF-0004", "FF", DeploymentMode.OnPrem, "5.1.0", null, "Errore FF-E2041", "Aggiornare la catena.", new DateOnly(2026, 9, 1))],
            [new ReleaseNoteView("RN-FF-5.2.0-03", "FF", "5.2.0", ReleaseNoteType.KnownIssue, "Allegati oltre 5 MB", "Fatture ferme.")]);
        await using WebApplicationFactory<WebEntryPoint> factory = Factory(new RoutingAgentHandler().On("/api/catalog", catalog));
        using HttpClient client = factory.CreateClient();

        //SUT
        string page = await client.GetStringAsync("/Data", CancellationToken);

        Assert.That(page, Does.Contain("FattureFlow").And.Contain("5.2.0"));
        Assert.That(page, Does.Contain("Studio Contabile Ferretti S.r.l.").And.Contain("on-premise"));
        Assert.That(page, Does.Contain("KB-FF-0004").And.Contain("5.1.0 e successive"));
        Assert.That(page, Does.Contain("RN-FF-5.2.0-03"));
    }

    [TestCase("/Runs")]
    [TestCase("/Outcomes")]
    [TestCase("/Data")]
    public async Task GetPage_AgentDown_PageStillRendersWithTheReason(string path)
    {
        //SETUP
        await using WebApplicationFactory<WebEntryPoint> factory = Factory(new RoutingAgentHandler());
        using HttpClient client = factory.CreateClient();

        //SUT
        using HttpResponseMessage response = await client.GetAsync(path, CancellationToken);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(CancellationToken), Does.Contain("id=\"agent-error\""));
    }

    private static WebApplicationFactory<WebEntryPoint> Factory(RoutingAgentHandler agent) =>
        new WebApplicationFactory<WebEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(AgentApiClient.BaseAddressConfigurationKey, "http://agent.test");
            builder.UseSetting(ConsoleLinks.ZammadUrlKey, "http://zammad.test:8080");
            builder.UseSetting(ConsoleLinks.DashboardUrlKey, "http://dashboard.test/");

            // Come in StatusPageTests: senza la resilienza standard un errore dell'Agent finto arriva subito alla pagina.
#pragma warning disable EXTEXP0001
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<AgentApiClient>()
                    .RemoveAllResilienceHandlers()
                    .ConfigurePrimaryHttpMessageHandler(() => agent));
#pragma warning restore EXTEXP0001
        });

    /// <summary>Agent finto: JSON per percorso (senza query), 404 per il resto; registra i percorsi chiesti con la query.</summary>
    private sealed class RoutingAgentHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = [];

        public List<string> Paths { get; } = [];

        public RoutingAgentHandler On(string path, object body)
        {
            _responses[path] = JsonSerializer.Serialize(body, TicketJson.Options);

            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);

            return Task.FromResult(_responses.TryGetValue(request.RequestUri.AbsolutePath, out string? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }
}
