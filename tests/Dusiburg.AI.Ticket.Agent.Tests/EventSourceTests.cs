using System.Net;
using System.Text;
using System.Text.Json;
using Dusiburg.AI.Ticket.Agent.Events;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Zammad;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Dusiburg.AI.Ticket.Agent.Tests;

/// <summary>Sorgenti degli eventi (P1): firma e payload del webhook, endpoint senza database, filtro del polling.</summary>
public class EventSourceTests
{
    private const string Secret = "segreto-di-prova";

    private WebApplicationFactory<AgentEntryPoint> _factory = null!;

    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [OneTimeSetUp]
    public void CreateFactory()
    {
        _factory = new WebApplicationFactory<AgentEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:sql", @"Server=(localdb)\non-usato;Database=Ticket_Test");
            builder.UseSetting(ZammadOptions.Keys.WebhookSecret, Secret);

            // Niente worker né polling: questi test non toccano il database.
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });
    }

    [OneTimeTearDown]
    public async Task DisposeFactory() => await _factory.DisposeAsync();

    [Test]
    public void Signature_HmacSha1OfTheRawBody()
    {
        //SETUP
        byte[] body = Encoding.UTF8.GetBytes("""{"ticket_id":3}""");
        string valid = WebhookEventSource.Sign(body, Secret);

        //SUT
        Assert.That(valid, Does.StartWith("sha1=").And.Length.EqualTo(45));
        Assert.That(WebhookEventSource.IsSignatureValid(body, valid, Secret), Is.True);
        Assert.That(WebhookEventSource.IsSignatureValid(body, valid.ToUpperInvariant().Replace("SHA1=", "sha1="), Secret), Is.True);
        Assert.That(WebhookEventSource.IsSignatureValid(body, valid, "altro-segreto"), Is.False);
        Assert.That(WebhookEventSource.IsSignatureValid(Encoding.UTF8.GetBytes("""{"ticket_id":4}"""), valid, Secret), Is.False);
        Assert.That(WebhookEventSource.IsSignatureValid(body, "sha1=zz", Secret), Is.False);
        Assert.That(WebhookEventSource.IsSignatureValid(body, "", Secret), Is.False);
    }

    [TestCase("""{"ticket_id":3,"ticket_number":"71003","article_id":9,"article_sender":"Customer","article_internal":false}""", 3, 9, "Customer", false)]
    [TestCase("""{"ticket_id":"3","article_id":"9","article_sender":"Agent","article_internal":"true"}""", 3, 9, "Agent", true)]
    public void TryParse_PayloadWithNativeOrStringValues(string json, long ticketId, long articleId, string sender, bool isInternal)
    {
        //SUT
        bool parsed = WebhookEventSource.TryParse(Encoding.UTF8.GetBytes(json), out TicketEvent? ticketEvent);

        Assert.That(parsed, Is.True);
        Assert.That(ticketEvent, Is.EqualTo(new TicketEvent(ticketId, articleId, sender, isInternal, RunTrigger.Webhook)));
    }

    [TestCase("""{"ticket_id":3}""")]
    [TestCase("""{"ticket_id":"#{ticket.x / no such method}","article_id":9}""")]
    [TestCase("non è json")]
    public void TryParse_MissingOrInvalidIds_IsRejected(string json)
    {
        //SUT
        Assert.That(WebhookEventSource.TryParse(Encoding.UTF8.GetBytes(json), out _), Is.False);
    }

    [Test]
    public async Task Post_WrongSignature_Unauthorized()
    {
        //SETUP
        using HttpClient client = _factory.CreateClient();

        //SUT
        using HttpResponseMessage response = await PostAsync(client, """{"ticket_id":3,"article_id":9,"article_sender":"Customer","article_internal":false}""", "sha1=00");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Post_InternalNoteOfTheAgent_AcceptedButIgnoredWithoutTouchingTheDatabase()
    {
        //SETUP
        using HttpClient client = _factory.CreateClient();
        const string body = """{"ticket_id":3,"article_id":10,"article_sender":"Agent","article_internal":true}""";

        //SUT
        using HttpResponseMessage response = await PostAsync(client, body, WebhookEventSource.Sign(Encoding.UTF8.GetBytes(body), Secret));

        using JsonDocument result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(result.RootElement.GetProperty("outcome").GetString(), Is.EqualTo(nameof(IntakeOutcome.Ignored)));
    }

    [Test]
    public async Task Post_SignedButInvalidPayload_BadRequest()
    {
        //SETUP
        using HttpClient client = _factory.CreateClient();
        const string body = """{"ticket_id":3}""";

        //SUT
        using HttpResponseMessage response = await PostAsync(client, body, WebhookEventSource.Sign(Encoding.UTF8.GetBytes(body), Secret));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public void Polling_OnlyPublicCustomerArticlesAfterTheStartAndACursorThatNeverGoesBack()
    {
        //SETUP
        var start = new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
        ZammadArticleDetail[] articles =
        [
            Article(1, "Customer", isInternal: false, start.AddMinutes(-5)),
            Article(2, "Customer", isInternal: false, start.AddMinutes(1)),
            Article(3, "Agent", isInternal: true, start.AddMinutes(2)),
            Article(4, "Agent", isInternal: false, start.AddMinutes(3)),
            Article(5, "Customer", isInternal: false, start.AddMinutes(4))
        ];
        ZammadTicketDetail[] tickets = [Ticket(1, start.AddMinutes(4)), Ticket(2, start.AddMinutes(2))];

        //SUT
        long[] accepted = [.. PollingEventSource.NewCustomerArticles(articles, start).Select(a => a.Id)];
        DateTimeOffset next = PollingEventSource.NextCursor(start, tickets);
        DateTimeOffset unchanged = PollingEventSource.NextCursor(start.AddMinutes(10), tickets);
        DateTimeOffset empty = PollingEventSource.NextCursor(start, []);

        Assert.That(accepted, Is.EqualTo(new long[] { 2, 5 }));
        Assert.That(next, Is.EqualTo(start.AddMinutes(4)));
        Assert.That(unchanged, Is.EqualTo(start.AddMinutes(10)));
        Assert.That(empty, Is.EqualTo(start));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string body, string signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/zammad") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(WebhookEventSource.SignatureHeader, signature);
        request.Headers.Add(WebhookEventSource.DeliveryHeader, Guid.NewGuid().ToString());

        return await client.SendAsync(request, CancellationToken);
    }

    private static ZammadArticleDetail Article(long id, string sender, bool isInternal, DateTimeOffset createdAt) =>
        new(id, 1, null, sender, "web", isInternal, null, "testo", "text/plain", null, createdAt);

    private static ZammadTicketDetail Ticket(long id, DateTimeOffset updatedAt) => new(id, $"7100{id}", "titolo", 1, null, null, updatedAt, updatedAt);
}
