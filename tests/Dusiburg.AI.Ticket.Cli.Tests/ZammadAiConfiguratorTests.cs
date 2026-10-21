using System.Text.Json.Nodes;
using Dusiburg.AI.Ticket.Cli.Configuration;
using Dusiburg.AI.Ticket.Cli.Tests.Support;
using Dusiburg.AI.Ticket.Cli.Zammad;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Cli.Tests;

/// <summary><c>zammad ai on|off|status</c> contro lo Zammad finto, dopo un bootstrap: configurazione completa, idempotenza, errori.</summary>
public class ZammadAiConfiguratorTests
{
    private static readonly ZammadAiSettings Settings = new(new Uri("http://172.24.176.1:11434"), "qwen3.5:9b");

    private string _secretsPath = null!;

    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [SetUp]
    public void CreateSecrets()
    {
        _secretsPath = Path.Combine(Path.GetTempPath(), $"ticket-secrets-{Guid.NewGuid():N}.json");
        File.WriteAllText(_secretsPath, new JsonObject { [UserSecretsStore.AdminTokenKey] = FakeZammadServer.AdminToken }.ToJsonString());
    }

    [TearDown]
    public void DeleteSecrets() => File.Delete(_secretsPath);

    [Test]
    public async Task On_AfterBootstrap_ConfiguresProviderFeaturesAgentAndTrigger()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = zammad.CreateServices();
        await BootstrapAsync(services);

        //SUT
        BootstrapReport report = await Configurator(services).OnAsync(CancellationToken);

        JsonObject connection = zammad.Resource("ai/provider_connections").Single();
        Assert.That((string?)connection["name"], Is.EqualTo(ZammadAiConfigurator.ConnectionName));
        Assert.That(((string?)connection["provider"], (string?)connection["config"]!["url"], (string?)connection["config"]!["model"]),
            Is.EqualTo(("ollama", "http://172.24.176.1:11434", "qwen3.5:9b")));
        Assert.That((bool?)connection["default_embedding"], Is.False, "embedding assegnato da Zammad e tolto");
        Assert.That(Setting(zammad, "ai_provider"), Is.True);
        Assert.That(ZammadAiConfigurator.FeatureSettings.Select(s => Setting(zammad, s)), Has.All.True);
        Assert.That(ZammadAiConfigurator.KnowledgeBaseSettings.Select(s => Setting(zammad, s)), Has.All.False);

        JsonObject agent = zammad.Resource("ai_agents").Single();
        Assert.That(((string?)agent["name"], (string?)agent["agent_type"]), Is.EqualTo((ZammadAiConfigurator.AgentName, "CustomAIAgent")));
        Assert.That(JsonNode.DeepEquals(agent["definition"], ZammadAiConfigurator.TriageDefinition()), Is.True);
        Assert.That(agent["action_definition"]!["mapping"]!["ticket.tags"]!["value"]!.GetValue<string>(), Is.EqualTo(ZammadDirectory.ZammadTriageTag));

        JsonObject trigger = zammad.Resource("triggers").Single(t => (string?)t["name"] == ZammadAiConfigurator.TriggerName);
        Assert.That((bool?)trigger["active"], Is.True);
        Assert.That(FakeZammadServer.Id(trigger["perform"]!["ai.ai_agent"]!["ai_agent_id"]![0]), Is.EqualTo(FakeZammadServer.Id(agent["id"])));
        Assert.That((string?)trigger["condition"]!["ticket.action"]!["value"], Is.EqualTo("create"));
        Assert.That(report.Changed, Is.Not.Empty);
    }

    [Test]
    public async Task On_ProviderEnabledOnlyAfterTheEmbeddingIsOff()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = zammad.CreateServices();
        await BootstrapAsync(services);
        int start = zammad.Requests.Count;

        //SUT
        await Configurator(services).OnAsync(CancellationToken);

        List<RecordedRequest> writes = [.. zammad.Requests.Skip(start).Where(r => r.Method != HttpMethod.Get)];
        int embeddingOff = writes.FindIndex(r => r.Path.EndsWith("/set_default", StringComparison.Ordinal));
        int providerOn = writes.FindIndex(r => r.Path == $"settings/{SettingId(zammad, "ai_provider")}");
        Assert.That(embeddingOff, Is.GreaterThanOrEqualTo(0));
        Assert.That(providerOn, Is.GreaterThan(embeddingOff), "con il provider spento Zammad non ricostruisce l'indice vettoriale");
    }

    [Test]
    public async Task On_SecondTime_ChangesNothing()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = zammad.CreateServices();
        await BootstrapAsync(services);
        await Configurator(services).OnAsync(CancellationToken);
        int start = zammad.Requests.Count;

        //SUT
        BootstrapReport second = await Configurator(services).OnAsync(CancellationToken);

        Assert.That(second.Changed, Is.Empty);
        Assert.That(zammad.Requests.Skip(start), Has.None.Matches<RecordedRequest>(r => r.Method != HttpMethod.Get), "solo letture");
    }

    [Test]
    public async Task On_DifferentModel_UpdatesTheConnection()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = zammad.CreateServices();
        await BootstrapAsync(services);
        await Configurator(services).OnAsync(CancellationToken);

        //SUT
        BootstrapReport report = await Configurator(services, Settings with { Model = "qwen3.5:4b" }).OnAsync(CancellationToken);

        Assert.That((string?)zammad.Resource("ai/provider_connections").Single()["config"]!["model"], Is.EqualTo("qwen3.5:4b"));
        Assert.That(report.Changed, Has.Exactly(1).Contains("aggiornata"));
    }

    [Test]
    public async Task On_WithoutBootstrap_IsRefusedWithoutWriting()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = zammad.CreateServices();

        //SUT
        Assert.That(async () => await Configurator(services).OnAsync(CancellationToken),
            Throws.InvalidOperationException.With.Message.Contains("zammad bootstrap"));
        Assert.That(zammad.Requests, Has.None.Matches<RecordedRequest>(r => r.Method != HttpMethod.Get));
    }

    [Test]
    public async Task On_OllamaUnreachableFromZammad_ExplainsThePrerequisites()
    {
        //SETUP
        var zammad = new FakeZammadServer { OllamaReachable = false };
        using ServiceProvider services = zammad.CreateServices();
        await BootstrapAsync(services);

        //SUT
        Assert.That(async () => await Configurator(services).OnAsync(CancellationToken),
            Throws.InvalidOperationException.With.Message.Contains("OLLAMA_HOST").And.Message.Contains("firewall"));
        Assert.That(Setting(zammad, "ai_provider"), Is.False);
    }

    [Test]
    public async Task Off_TurnsOffFeaturesProviderAndTriggerKeepingTheConfiguration()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = zammad.CreateServices();
        await BootstrapAsync(services);
        await Configurator(services).OnAsync(CancellationToken);

        //SUT
        await Configurator(services).OffAsync(CancellationToken);

        Assert.That(Setting(zammad, "ai_provider"), Is.False);
        Assert.That(ZammadAiConfigurator.FeatureSettings.Select(s => Setting(zammad, s)), Has.All.False);
        Assert.That((bool?)zammad.Resource("triggers").Single(t => (string?)t["name"] == ZammadAiConfigurator.TriggerName)["active"], Is.False);
        Assert.That((zammad.Resource("ai/provider_connections").Count, zammad.Resource("ai_agents").Count), Is.EqualTo((1, 1)), "configurazione conservata");
    }

    [Test]
    public async Task Status_AfterOn_ReportsReachabilityModelAndSwitches()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = zammad.CreateServices();
        await BootstrapAsync(services);
        await Configurator(services).OnAsync(CancellationToken);

        //SUT
        IReadOnlyList<string> status = await Configurator(services).StatusAsync(CancellationToken);

        Assert.That(status, Has.Some.Contains("il provider risponde").And.Some.Contains("qwen3.5:9b presente"));
        Assert.That(status, Has.Some.EqualTo("ai_provider: acceso"));
        Assert.That(status, Has.Some.Contains($"Trigger '{ZammadAiConfigurator.TriggerName}': attivo"));
    }

    [Test]
    public async Task Status_OllamaUnreachable_SaysSo()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = zammad.CreateServices();
        await BootstrapAsync(services);
        await Configurator(services).OnAsync(CancellationToken);
        zammad.OllamaReachable = false;

        //SUT
        IReadOnlyList<string> status = await Configurator(services).StatusAsync(CancellationToken);

        Assert.That(status, Has.Some.Contains("il provider non risponde: Connection refused"));
    }

    private async Task BootstrapAsync(ServiceProvider services) =>
        await new ZammadBootstrapper(
            services.GetRequiredService<ZammadClient>(),
            new UserSecretsStore(_secretsPath),
            new BootstrapSettings(new Uri("http://172.24.176.1:5401/webhooks/zammad"), [], TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10)),
            TextWriter.Null,
            TimeProvider.System).RunAsync(rotateToken: false, CancellationToken);

    private static ZammadAiConfigurator Configurator(ServiceProvider services, ZammadAiSettings? settings = null) =>
        new(services.GetRequiredService<ZammadClient>(), settings ?? Settings, TextWriter.Null);

    private static bool Setting(FakeZammadServer zammad, string name) =>
        zammad.Resource("settings").Single(s => (string?)s["name"] == name)["state_current"]!["value"]!.GetValue<bool>();

    private static long SettingId(FakeZammadServer zammad, string name) =>
        FakeZammadServer.Id(zammad.Resource("settings").Single(s => (string?)s["name"] == name)["id"]);
}
