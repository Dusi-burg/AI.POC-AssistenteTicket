using System.Net;
using System.Text;
using Dusiburg.AI.Ticket.Web.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Web.Tests;

/// <summary>Pagina "Stato" con un Agent finto: nessun servizio vero.</summary>
public class StatusPageTests
{
    private const string HealthJson =
        """
        {"checks":[
          {"number":1,"name":"Connessione SQL","status":"Ok","detail":"(localdb)\\localdev","duration":"00:00:00.0120000"},
          {"number":4,"name":"Database applicativo","status":"Warning","detail":"il database 'Ticket' non esiste ancora","duration":"00:00:00"},
          {"number":12,"name":"Token Zammad","status":"Error","detail":"token rifiutato","duration":"00:00:00"}
        ]}
        """;

    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [Test]
    public async Task GetIndex_AgentAnswers_ShowsEveryCheckAndTheZammadLink()
    {
        //SETUP
        await using WebApplicationFactory<WebEntryPoint> factory = Factory(new FakeAgentHandler(HttpStatusCode.OK, HealthJson));
        using HttpClient client = factory.CreateClient();

        //SUT
        string page = await client.GetStringAsync("/", CancellationToken);

        Assert.That(page, Does.Contain("Connessione SQL").And.Contain("Database applicativo").And.Contain("Token Zammad"));
        Assert.That(page, Does.Contain("text-bg-warning").And.Contain("text-bg-danger"));
        Assert.That(page, Does.Contain("href=\"http://zammad.test:8080\""));
        Assert.That(page, Does.Not.Contain("id=\"agent-error\""));
    }

    [Test]
    public async Task GetIndex_AgentDown_PageStillRendersWithTheReason()
    {
        //SETUP
        await using WebApplicationFactory<WebEntryPoint> factory = Factory(new FakeAgentHandler(HttpStatusCode.ServiceUnavailable, ""));
        using HttpClient client = factory.CreateClient();

        //SUT
        using HttpResponseMessage response = await client.GetAsync("/", CancellationToken);
        string page = await response.Content.ReadAsStringAsync(CancellationToken);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(page, Does.Contain("id=\"agent-error\"").And.Contain("Agent non raggiungibile"));
        Assert.That(page, Does.Not.Contain("id=\"health\""));
    }

    private static WebApplicationFactory<WebEntryPoint> Factory(FakeAgentHandler agent) =>
        new WebApplicationFactory<WebEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(AgentApiClient.BaseAddressConfigurationKey, "http://agent.test");
            builder.UseSetting(ConsoleLinks.ZammadUrlKey, "http://zammad.test:8080");

            // Senza la resilienza standard verso l'Agent finto: il 503 arriva subito alla pagina invece di essere ritentato per
            // una decina di secondi. RemoveAllResilienceHandlers è sperimentale (Microsoft.Extensions.Http.Resilience 10.11).
#pragma warning disable EXTEXP0001
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<AgentApiClient>()
                    .RemoveAllResilienceHandlers()
                    .ConfigurePrimaryHttpMessageHandler(() => agent));
#pragma warning restore EXTEXP0001
        });

    private sealed class FakeAgentHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
