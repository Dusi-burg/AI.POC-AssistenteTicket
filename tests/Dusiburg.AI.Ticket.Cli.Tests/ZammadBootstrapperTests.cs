using System.Text.Json.Nodes;
using Dusiburg.AI.Ticket.Cli.Configuration;
using Dusiburg.AI.Ticket.Cli.Tests.Support;
using Dusiburg.AI.Ticket.Cli.Zammad;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Cli.Tests;

/// <summary><c>zammad bootstrap</c> contro uno Zammad finto con stato: struttura completa, una migrazione, segreti, idempotenza.</summary>
public class ZammadBootstrapperTests
{
    private static readonly string[] Operators = ["Marta Ferrari", "Luca Conti"];

    private string _secretsPath = null!;

    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [SetUp]
    public void CreateSecrets()
    {
        _secretsPath = Path.Combine(Path.GetTempPath(), $"ticket-secrets-{Guid.NewGuid():N}.json");

        // Primo giro come sulla macchina dell'utente: sotto la chiave dell'Agent c'è il token dell'amministratore.
        File.WriteAllText(_secretsPath, new JsonObject
        {
            [UserSecretsStore.ApiTokenKey] = FakeZammadServer.AdminToken,
            [UserSecretsStore.SqlConnectionStringKey] = "Server=(localdb)\\localdev;Database=Ticket"
        }.ToJsonString());
    }

    [TearDown]
    public void DeleteSecrets() => File.Delete(_secretsPath);

    [Test]
    public async Task Run_EmptyInstance_CreatesTheWholeStructureWithASingleMigration()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = zammad.CreateServices();

        //SUT
        BootstrapReport report = await Bootstrapper(services).RunAsync(rotateToken: false, CancellationToken);

        Assert.That(zammad.Resource("groups").Select(g => (string?)g["name"]), Is.SupersetOf(ZammadDirectory.Groups));
        Assert.That(zammad.Resource("groups").Where(g => ZammadDirectory.Groups.Contains((string?)g["name"])), Has.All.Matches<JsonObject>(g => (bool?)g["shared_drafts"] == true));
        Assert.That(zammad.Resource("object_manager_attributes").Count(a => (string?)a["name"] != "title"), Is.EqualTo(ZammadDirectory.Attributes.Count));
        Assert.That(zammad.Requests.Count(r => r.Path == "object_manager_attributes_execute_migrations"), Is.EqualTo(1), "una sola migrazione");
        Assert.That(zammad.Resource("tag_list").Select(t => (string?)t["name"]), Is.EquivalentTo(ZammadDirectory.Tags));
        Assert.That(zammad.Resource("users").Select(u => (string?)u["login"]),
            Is.SupersetOf(new[] { ZammadDirectory.AssistantLogin, "marta.ferrari@dusiburg-software.example.it", "luca.conti@dusiburg-software.example.it" }));
        Assert.That(zammad.Resource("triggers").Select(t => (string?)t["name"]), Is.EquivalentTo(ZammadDirectory.TriggerNames));
        Assert.That(zammad.Resource("triggers").Select(t => t["active"]!.GetValue<bool>()), Has.All.False, "attivi solo con 'zammad trigger on'");
        Assert.That(zammad.Resource("webhooks").Single()["custom_payload"]!.GetValue<string>(), Does.Contain("\"ticket_state\""));
        Assert.That(report.Changed, Is.Not.Empty);
    }

    [Test]
    public async Task Run_FirstTime_MovesTheAdminTokenAndStoresTheAssistantTokenAndSecret()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = zammad.CreateServices();
        var secrets = new UserSecretsStore(_secretsPath);

        //SUT
        await Bootstrapper(services).RunAsync(rotateToken: false, CancellationToken);

        ZammadUser assistant = await services.GetRequiredService<ZammadClient>().GetUserOfTokenAsync(secrets.Get(UserSecretsStore.ApiTokenKey)!, CancellationToken);
        Assert.That(assistant.Login, Is.EqualTo(ZammadDirectory.AssistantLogin));
        Assert.That(secrets.Get(UserSecretsStore.AdminTokenKey), Is.EqualTo(FakeZammadServer.AdminToken));
        Assert.That(secrets.Get(UserSecretsStore.WebhookSecretKey), Does.Match("^[0-9a-f]{64}$"));
        Assert.That(secrets.Get(UserSecretsStore.SqlConnectionStringKey), Is.Not.Null, "le altre chiavi restano");
        Assert.That(zammad.Requests.Single(r => r.Method == HttpMethod.Post && r.Path == "user_access_token").From, Is.EqualTo(ZammadDirectory.AssistantLogin));
    }

    [Test]
    public async Task Run_SecondTime_ChangesNothing()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = zammad.CreateServices();
        await Bootstrapper(services).RunAsync(rotateToken: false, CancellationToken);
        int requestsAfterFirstRun = zammad.Requests.Count;

        //SUT
        BootstrapReport second = await Bootstrapper(services).RunAsync(rotateToken: false, CancellationToken);

        Assert.That(second.Changed, Is.Empty);
        Assert.That(zammad.Requests.Skip(requestsAfterFirstRun), Has.None.Matches<RecordedRequest>(r => r.Method != HttpMethod.Get), "solo letture");
    }

    [Test]
    public async Task Run_RotateToken_ReplacesTheAssistantToken()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = zammad.CreateServices();
        var secrets = new UserSecretsStore(_secretsPath);
        await Bootstrapper(services).RunAsync(rotateToken: false, CancellationToken);
        string first = secrets.Get(UserSecretsStore.ApiTokenKey)!;

        //SUT
        await Bootstrapper(services).RunAsync(rotateToken: true, CancellationToken);

        Assert.That(secrets.Get(UserSecretsStore.ApiTokenKey), Is.Not.EqualTo(first));
        Assert.That(async () => await services.GetRequiredService<ZammadClient>().GetUserOfTokenAsync(first, CancellationToken),
            Throws.TypeOf<ZammadApiException>(), "il token vecchio è stato cancellato");
    }

    [Test]
    public async Task Run_TokenOfANonAdmin_IsRefused()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        zammad.AddUser("agente@example.it", [2], token: "agent-token");
        using ServiceProvider services = zammad.CreateServices("agent-token");

        //SUT
        Assert.That(async () => await Bootstrapper(services).RunAsync(rotateToken: false, CancellationToken),
            Throws.InvalidOperationException.With.Message.Contains("non è amministratore"));
        Assert.That(zammad.Requests, Has.None.Matches<RecordedRequest>(r => r.Method != HttpMethod.Get));
    }

    [Test]
    public async Task Run_Always_GivesTheAdminAccessToThePocGroupsKeepingTheOthers()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = zammad.CreateServices();

        //SUT
        await Bootstrapper(services).RunAsync(rotateToken: false, CancellationToken);

        JsonObject groupIds = zammad.Resource("users").Single(u => FakeZammadServer.Id(u["id"]) == FakeZammadServer.AdminId)["group_ids"]!.AsObject();
        Assert.That(groupIds.Count, Is.EqualTo(1 + ZammadDirectory.Groups.Count), "gruppo Users e i tre del POC");
    }

    private ZammadBootstrapper Bootstrapper(ServiceProvider services) => new(
        services.GetRequiredService<ZammadClient>(),
        new UserSecretsStore(_secretsPath),
        new BootstrapSettings(new Uri("http://172.24.176.1:5401/webhooks/zammad"), Operators, TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10)),
        TextWriter.Null,
        TimeProvider.System);
}
