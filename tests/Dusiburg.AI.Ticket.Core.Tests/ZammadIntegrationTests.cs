using System.Text.Json;
using Dusiburg.AI.Ticket.Core.Composition;
using Dusiburg.AI.Ticket.Core.Context;
using Dusiburg.AI.Ticket.Core.Publishing;
using Dusiburg.AI.Ticket.Core.Tests.Support;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Core.Tests;

/// <summary>Contesto letto da Zammad (P4) e pubblicazione (P3, P8), su uno Zammad finto.</summary>
public class ZammadIntegrationTests
{
    [Test]
    public async Task Publish_SharedDraftWithDisclosureThenInternalNoteThenTags()
    {
        //SETUP
        FakeZammad zammad = new FakeZammad()
            .On("PUT", "/api/v1/tickets/170/shared_draft", "{}")
            .On("POST", "/api/v1/ticket_articles", """{"id":431,"ticket_id":170,"internal":true,"body":"x","created_at":"2026-10-16T10:00:00Z"}""")
            .On("POST", "/api/v1/tags/add", "true");
        ComposedDraft draft = DraftComposer.Compose(DraftKind.Clarification, "", ["Quale versione?"], [], "Paola");

        //SUT
        PublicationResult publication = await new ZammadPublisher(zammad.Client()).PublishAsync(170, draft, "<p>nota</p>", CancellationToken.None);

        var writes = zammad.Writes.ToList();
        Assert.That(writes.Select(w => (w.Method, w.Path)), Is.EqualTo(new[]
        {
            ("PUT", "/api/v1/tickets/170/shared_draft"), ("POST", "/api/v1/ticket_articles"), ("POST", "/api/v1/tags/add"), ("POST", "/api/v1/tags/add")
        }));

        using JsonDocument shared = JsonDocument.Parse(writes[0].Body!);
        JsonElement article = shared.RootElement.GetProperty("new_article");
        Assert.That(article.GetProperty("body").GetString(), Does.Contain(DraftComposer.AiDisclosure));
        Assert.That((article.GetProperty("content_type").GetString(), article.GetProperty("internal").GetBoolean()), Is.EqualTo(("text/html", false)));

        using JsonDocument note = JsonDocument.Parse(writes[1].Body!);
        Assert.That(note.RootElement.GetProperty("internal").GetBoolean(), Is.True);
        Assert.That(publication.Tags, Is.EqualTo(new[] { ZammadPublisher.DraftTag, ZammadPublisher.LowConfidenceTag }));
        Assert.That(publication.NoteArticleId, Is.EqualTo(431), "riferimento per l'esito della Fase 7");
        Assert.That(writes[2].Body, Does.Contain("\"item\":\"ai-bozza\"").And.Contain("\"o_id\":170"));
    }

    [Test]
    public void Publish_DraftWithoutDisclosure_NothingIsWritten()
    {
        //SETUP
        var zammad = new FakeZammad();
        var draft = new ComposedDraft(DraftKind.Answer, "testo", "<p>testo</p>", []);

        //SUT
        Assert.That(() => new ZammadPublisher(zammad.Client()).PublishAsync(170, draft, "<p>nota</p>", CancellationToken.None),
            Throws.InvalidOperationException.With.Message.Contains("dicitura"));
        Assert.That(zammad.Requests, Is.Empty);
    }

    [Test]
    public async Task Load_ContextFromTheOrganizationAndThreadWithoutInternalNotes()
    {
        //SETUP
        FakeZammad zammad = new FakeZammad()
            .On("GET", "/api/v1/tickets/170", """{"id":170,"number":"71170","title":"Errore FF-E2041","group_id":2,"customer_id":59,"organization_id":7,"created_at":"2026-10-16T09:00:00Z","updated_at":"2026-10-16T09:00:00Z"}""")
            .On("GET", "/api/v1/ticket_articles/by_ticket/170", """
                [
                  {"id":429,"ticket_id":170,"sender":"Customer","type":"web","internal":false,"body":"Le fatture vanno in errore.\n\nGrazie\nGiorgio Ferretti\nTel. 02 25279401","content_type":"text/plain","created_at":"2026-10-16T09:00:00Z"},
                  {"id":430,"ticket_id":170,"sender":"Agent","type":"note","internal":true,"body":"<p>nota dell'assistente</p>","content_type":"text/html","created_at":"2026-10-16T09:01:00Z"},
                  {"id":431,"ticket_id":170,"sender":"Agent","type":"note","internal":false,"body":"<p>Quale versione?</p>","content_type":"text/html","created_at":"2026-10-16T09:05:00Z"},
                  {"id":432,"ticket_id":170,"sender":"Customer","type":"web","internal":false,"body":"La 5.1.2.","content_type":"text/plain","created_at":"2026-10-16T09:10:00Z"}
                ]
                """)
            .On("GET", "/api/v1/users/59", """{"id":59,"login":"giorgio.ferretti@ferretti.example.it","email":"giorgio.ferretti@ferretti.example.it","firstname":"Giorgio","lastname":"Ferretti"}""")
            .On("GET", "/api/v1/organizations/7", """{"id":7,"name":"Studio Contabile Ferretti S.r.l.","prodotto":"FF","versione_prodotto":"5.1.2","modalita":"onprem","livello_sla":"plus"}""");
        var loader = new TicketContextLoader(zammad.Client(), null!);

        //SUT
        IncomingTicket ticket = await loader.LoadAsync(170, triggerArticleId: 432, CancellationToken.None);

        Assert.That(ticket.Customer, Is.EqualTo(new CustomerContext("Studio Contabile Ferretti S.r.l.", "Giorgio Ferretti", "giorgio.ferretti@ferretti.example.it",
            "FF", "5.1.2", DeploymentMode.OnPrem, SlaLevel.Plus, "organizzazione Zammad")));
        Assert.That(ticket.Thread.Select(m => (m.ArticleId, m.Author, m.Text)), Is.EqualTo(new[]
        {
            (429L, MessageAuthor.Customer, "Le fatture vanno in errore."),
            (431L, MessageAuthor.Agent, "Quale versione?"),
            (432L, MessageAuthor.Customer, "La 5.1.2.")
        }));
        Assert.That(ticket.TriggerArticleId, Is.EqualTo(432));
    }

    [TestCase("onprem", DeploymentMode.OnPrem)]
    [TestCase("saas", DeploymentMode.Saas)]
    [TestCase("", null)]
    [TestCase(null, null)]
    public void ParseMode_ValuesOfTheBootstrapField(string? value, DeploymentMode? expected)
    {
        //SUT
        Assert.That(TicketContextLoader.ParseMode(value), Is.EqualTo(expected));
    }
}
