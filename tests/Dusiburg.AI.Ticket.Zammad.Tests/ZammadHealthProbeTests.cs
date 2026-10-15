using System.Net;
using Dusiburg.AI.Ticket.Shared.Health;
using Dusiburg.AI.Ticket.Zammad.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Zammad.Tests;

/// <summary>Controlli 11 e 12 di <c>/api/health</c> contro uno Zammad finto.</summary>
public class ZammadHealthProbeTests
{
    private const string UsersMe = "/api/v1/users/me?expand=true";

    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [Test]
    public async Task RunAsync_AgentToken_BothOk()
    {
        //SETUP
        var handler = new FakeZammadHandler()
            .Respond("/", HttpStatusCode.OK, "<html></html>")
            .Respond(UsersMe, HttpStatusCode.OK, """{"id":7,"login":"ai-assistant","roles":["Agent"]}""");

        //SUT
        IReadOnlyList<ProbeResult> results = await RunAsync(handler, "token");

        Assert.That(results.Select(r => (r.Number, r.Status)), Is.EqualTo(new[] { (11, ProbeStatus.Ok), (12, ProbeStatus.Ok) }));
        Assert.That(results[1].Detail, Does.Contain("ai-assistant").And.Contain("Agent"));
    }

    [Test]
    public async Task RunAsync_AdminToken_WarningPointingToAiAssistant()
    {
        //SETUP
        var handler = new FakeZammadHandler()
            .Respond("/", HttpStatusCode.OK, "<html></html>")
            .Respond(UsersMe, HttpStatusCode.OK, """{"id":3,"login":"admin@dusiburg.example","roles":["Agent","Admin"]}""");

        //SUT
        IReadOnlyList<ProbeResult> results = await RunAsync(handler, "token");

        Assert.That(results[1].Status, Is.EqualTo(ProbeStatus.Warning));
        Assert.That(results[1].Detail, Does.Contain("ai-assistant"));
    }

    [Test]
    public async Task RunAsync_StackRestarting_ErrorAndTokenSkipped()
    {
        //SETUP
        var handler = new FakeZammadHandler().Respond("/", HttpStatusCode.BadGateway, "<html>502 Bad Gateway</html>");

        //SUT
        IReadOnlyList<ProbeResult> results = await RunAsync(handler, "token");

        Assert.That(results.Select(r => r.Status), Is.All.EqualTo(ProbeStatus.Error));
        Assert.That(results[0].Detail, Does.Contain("502"));
        Assert.That(results[1].Detail, Does.StartWith("non eseguito"));
        Assert.That(handler.Requests, Has.Count.EqualTo(1), "senza Zammad il token non si prova");
    }

    [Test]
    public async Task RunAsync_NoToken_ErrorNamingTheParameter()
    {
        //SETUP
        var handler = new FakeZammadHandler().Respond("/", HttpStatusCode.OK, "<html></html>");

        //SUT
        IReadOnlyList<ProbeResult> results = await RunAsync(handler, apiToken: null);

        Assert.That(results[1].Status, Is.EqualTo(ProbeStatus.Error));
        Assert.That(results[1].Detail, Does.Contain("Parameters:zammad-api-token"));
    }

    [Test]
    public async Task RunAsync_TokenRejected_ErrorAskingToRegenerate()
    {
        //SETUP
        var handler = new FakeZammadHandler()
            .Respond("/", HttpStatusCode.OK, "<html></html>")
            .Respond(UsersMe, HttpStatusCode.Unauthorized, """{"error":"Invalid token!"}""");

        //SUT
        IReadOnlyList<ProbeResult> results = await RunAsync(handler, "token-revocato");

        Assert.That(results[1].Status, Is.EqualTo(ProbeStatus.Error));
        Assert.That(results[1].Detail, Does.Contain("rigenerarlo").And.Not.Contain("token-revocato"));
    }

    private static async Task<IReadOnlyList<ProbeResult>> RunAsync(FakeZammadHandler handler, string? apiToken)
    {
        using ServiceProvider services = handler.CreateServices(apiToken);
        IHealthProbe probe = services.GetServices<IHealthProbe>().Single();

        return await probe.RunAsync(CancellationToken);
    }
}
