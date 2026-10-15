using System.Net;
using Dusiburg.AI.Ticket.Zammad.Api;
using Dusiburg.AI.Ticket.Zammad.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Zammad.Tests;

/// <summary>Client tipizzato contro uno Zammad finto: percorsi, header del token, JSON snake_case, errori.</summary>
public class ZammadClientTests
{
    private const string UsersMe = "/api/v1/users/me?expand=true";

    private const string AssistantJson =
        """{"id":7,"login":"ai-assistant@dusiburg.example","firstname":"Assistente","lastname":"AI","roles":["Agent"],"organization_id":null}""";

    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [Test]
    public async Task GetCurrentUserAsync_ValidToken_SendsTokenHeaderAndReadsSnakeCase()
    {
        //SETUP
        var handler = new FakeZammadHandler().Respond(UsersMe, HttpStatusCode.OK, AssistantJson);
        using ServiceProvider services = handler.CreateServices("token-di-prova");
        ZammadClient client = services.GetRequiredService<ZammadClient>();

        //SUT
        ZammadUser user = await client.GetCurrentUserAsync(CancellationToken);

        Assert.That((user.Id, user.Login, user.Firstname, user.IsAdmin), Is.EqualTo((7L, "ai-assistant@dusiburg.example", "Assistente", false)));
        Assert.That(user.Roles, Is.EqualTo(new[] { "Agent" }));
        Assert.That(handler.Requests.Single().RequestUri, Is.EqualTo(new Uri("http://zammad.test:8080" + UsersMe)));
        Assert.That(handler.Requests.Single().Headers.Authorization?.ToString(), Is.EqualTo("Token token=token-di-prova"));
    }

    [Test]
    public async Task GetCurrentUserAsync_TokenRejected_ThrowsWithZammadMessage()
    {
        //SETUP
        var handler = new FakeZammadHandler().Respond(UsersMe, HttpStatusCode.Unauthorized, """{"error":"Invalid token!"}""");
        using ServiceProvider services = handler.CreateServices("token-revocato");
        ZammadClient client = services.GetRequiredService<ZammadClient>();

        //SUT
        Assert.That(async () => await client.GetCurrentUserAsync(CancellationToken),
            Throws.TypeOf<ZammadApiException>()
                .With.Property(nameof(ZammadApiException.StatusCode)).EqualTo(HttpStatusCode.Unauthorized)
                .And.Message.Contains("Invalid token!"));
    }

    [Test]
    public async Task PingAsync_HomePage_AsksForHtmlNotJson()
    {
        //SETUP
        var handler = new FakeZammadHandler().Respond("/", HttpStatusCode.OK, "<html></html>");
        using ServiceProvider services = handler.CreateServices("token");
        ZammadClient client = services.GetRequiredService<ZammadClient>();

        //SUT
        await client.PingAsync(CancellationToken);

        Assert.That(handler.Requests.Single().Headers.Accept.Select(a => a.MediaType), Is.EqualTo(new[] { "text/html" }),
            "con Accept: application/json la pagina iniziale di Zammad risponde 500");
    }

    [Test]
    public async Task PingAsync_NoToken_NoAuthorizationHeader()
    {
        //SETUP
        var handler = new FakeZammadHandler().Respond("/", HttpStatusCode.OK, "<html></html>");
        using ServiceProvider services = handler.CreateServices(apiToken: null);
        ZammadClient client = services.GetRequiredService<ZammadClient>();

        //SUT
        HttpStatusCode status = await client.PingAsync(CancellationToken);

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(handler.Requests.Single().Headers.Authorization, Is.Null);
    }

    [TestCase(null, "http://127.0.0.1:8080/")]
    [TestCase("http://zammad.local:8080", "http://zammad.local:8080/")]
    [TestCase("http://zammad.local/helpdesk/", "http://zammad.local/helpdesk/")]
    public void Options_BaseUrl_DefaultAndTrailingSlash(string? configured, string expected)
    {
        //SETUP
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ZammadOptions.Keys.BaseUrl] = configured })
            .Build();

        //SUT
        ZammadOptions options = ZammadOptions.FromConfiguration(configuration);

        Assert.That(options.BaseUrl, Is.EqualTo(new Uri(expected)));
        Assert.That(options.HasApiToken, Is.False);
    }

    [Test]
    public void Options_InvalidUrl_ThrowsNamingTheKey()
    {
        //SETUP
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ZammadOptions.Keys.BaseUrl] = "127.0.0.1:8080" })
            .Build();

        //SUT
        Assert.That(() => ZammadOptions.FromConfiguration(configuration), Throws.InvalidOperationException.With.Message.Contains("ZAMMAD_URL"));
    }
}
