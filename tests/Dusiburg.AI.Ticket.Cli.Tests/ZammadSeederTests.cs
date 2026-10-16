using System.Text.Json.Nodes;
using Dusiburg.AI.Ticket.Cli.Configuration;
using Dusiburg.AI.Ticket.Cli.Tests.Support;
using Dusiburg.AI.Ticket.Cli.Zammad;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Seed;
using Dusiburg.AI.Ticket.DbInit;
using Dusiburg.AI.Ticket.Shared.Demo;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Cli.Tests;

/// <summary>
/// <c>zammad seed</c> e <c>demo</c> contro lo Zammad finto, con il database di <c>DbInit</c> (dati simulati veri). Il database ha la
/// colonna <c>VECTOR</c>: serve LocalDB 17, che la CI può non avere.
/// </summary>
[Category("RequiresSql2025")]
public class ZammadSeederTests
{
    private const int Dimensions = 4;
    private const int Count = 5;

    private string _connectionString = null!;
    private SeedData _data = null!;
    private string _secretsPath = null!;

    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [OneTimeSetUp]
    public async Task CreateDatabase()
    {
        _connectionString = TicketDatabaseInitializer.LocalConnectionStringFor($"Ticket_Test_{Guid.NewGuid():N}");
        _data = (await TicketDatabaseInitializer.RecreateAsync(_connectionString, Dimensions, seed: true, CancellationToken))!;
    }

    [OneTimeTearDown]
    public async Task DropDatabase() => await TicketDatabaseInitializer.DropAsync(_connectionString);

    [SetUp]
    public void CreateSecrets()
    {
        _secretsPath = Path.Combine(Path.GetTempPath(), $"ticket-secrets-{Guid.NewGuid():N}.json");
        File.WriteAllText(_secretsPath, new JsonObject { [UserSecretsStore.ApiTokenKey] = FakeZammadServer.AdminToken }.ToJsonString());
    }

    [TearDown]
    public void DeleteSecrets() => File.Delete(_secretsPath);

    [Test]
    public async Task Seed_AfterBootstrap_ImportsTheMostRecentTicketsInImportMode()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = await BootstrappedAsync(zammad);
        await using TicketDbContext db = Open();

        //SUT
        ZammadSeedReport report = await Seeder(services, db).RunAsync(Count, CancellationToken);

        Assert.That((report.OrganizationsCreated, report.TicketsCreated), Is.EqualTo((_data.Customers.Count, Count)));
        Assert.That(zammad.ImportModeChanges, Is.EqualTo(new[] { true, false }), "import_mode acceso e poi rispento");
        Assert.That(zammad.Resource("organizations"), Has.All.Matches<JsonObject>(o => (bool?)o["domain_assignment"] == true && o["prodotto"] is not null));
        Assert.That(zammad.Resource("tickets").Select(t => (string?)t["rif_storico"]), Is.All.Match("^HT-20"));

        // Ticket e articoli per conto di chi li ha scritti: cliente o operatore, mai l'amministratore.
        Assert.That(zammad.Requests.Where(r => r.Method == HttpMethod.Post && r.Path is "tickets" or "ticket_articles").Select(r => r.From),
            Has.All.EndsWith(".example.it"));
        Assert.That(await db.HistoricalTickets.CountAsync(t => t.ZammadTicketId != null, CancellationToken), Is.EqualTo(Count));
    }

    [Test]
    public async Task Seed_SecondTime_SkipsTheTicketsAlreadyImported()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = await BootstrappedAsync(zammad);
        await using TicketDbContext db = Open();
        await Seeder(services, db).RunAsync(Count, CancellationToken);
        int ticketsAfterFirstRun = zammad.Resource("tickets").Count;

        //SUT
        ZammadSeedReport second = await Seeder(services, db).RunAsync(Count, CancellationToken);

        Assert.That((second.OrganizationsCreated, second.CustomersCreated, second.TicketsCreated, second.TicketsPresent, second.TitlesUpdated),
            Is.EqualTo((0, 0, 0, Count, 0)));
        Assert.That(zammad.Resource("tickets"), Has.Count.EqualTo(ticketsAfterFirstRun));
        Assert.That(zammad.ImportModeChanges, Has.Count.EqualTo(2), "nessun nuovo giro in import_mode");
    }

    [Test]
    public async Task Seed_TicketPresentWithAnOldTitle_GetsTheSubjectOfTheDatabase()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = await BootstrappedAsync(zammad);
        await using TicketDbContext db = Open();
        await Seeder(services, db).RunAsync(Count, CancellationToken);
        JsonObject stale = zammad.Resource("tickets")[0];
        string number = (string)stale["rif_storico"]!;
        stale["title"] = "Errore {codice} nell'invio";

        //SUT
        ZammadSeedReport report = await Seeder(services, db).RunAsync(Count, CancellationToken);

        string subject = await db.HistoricalTickets.Where(t => t.Number == number).Select(t => t.Subject).SingleAsync(CancellationToken);
        Assert.That(report.TitlesUpdated, Is.EqualTo(1));
        Assert.That((string?)stale["title"], Is.EqualTo(subject));
    }

    [Test]
    public async Task Seed_ArticleRejected_SwitchesImportModeOffAnyway()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = await BootstrappedAsync(zammad);
        await using TicketDbContext db = Open();
        zammad.FailOn(HttpMethod.Post, "ticket_articles");

        //SUT
        Assert.That(async () => await Seeder(services, db).RunAsync(Count, CancellationToken), Throws.TypeOf<ZammadApiException>());

        Assert.That(zammad.ImportModeChanges, Is.EqualTo(new[] { true, false }));
    }

    [Test]
    public async Task Seed_WithoutBootstrap_IsRefused()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = zammad.CreateServices();
        await using TicketDbContext db = Open();

        //SUT
        Assert.That(async () => await Seeder(services, db).RunAsync(Count, CancellationToken),
            Throws.InvalidOperationException.With.Message.Contains("zammad bootstrap"));
    }

    [Test]
    public async Task Demo_AfterSeed_CreatesTheTicketOnBehalfOfTheScenarioCustomer()
    {
        //SETUP
        var zammad = new FakeZammadServer();
        using ServiceProvider services = await BootstrappedAsync(zammad);
        await using TicketDbContext db = Open();
        await Seeder(services, db).RunAsync(Count, CancellationToken);
        DemoScenario scenario = DemoCatalog.Scenarios.Single(s => s.Number == 4);

        //SUT
        DemoTicketCreated created = await new DemoTicketCreator(services.GetRequiredService<ZammadClient>(), db, SeedData.FindDataDirectory(AppContext.BaseDirectory)!)
            .CreateAsync(scenario.Number, CancellationToken);

        RecordedRequest request = zammad.Requests.Last(r => r.Method == HttpMethod.Post && r.Path == "tickets");
        Assert.That(created.CustomerEmail, Does.EndWith("@" + scenario.CustomerEmailDomain));
        Assert.That(request.From, Is.EqualTo(created.CustomerEmail));
        Assert.That((string?)request.Body!["article"]!["body"], Does.Contain(DemoCatalog.ErrorCode(scenario.ErrorCode!).Code));
        Assert.That(created.Group, Is.EqualTo(ZammadDirectory.InvoicingGroup));
    }

    [Test]
    public void Demo_EveryScenario_HasItsText()
    {
        //SUT
        IReadOnlyList<DemoTicketDefinition> tickets = DemoTicketCreator.Load(SeedData.FindDataDirectory(AppContext.BaseDirectory)!);

        Assert.That(tickets.Select(t => t.Scenario), Is.EquivalentTo(DemoCatalog.Scenarios.Select(s => s.Number)));
        Assert.That(DemoCatalog.Scenarios.Where(s => s.ErrorCode is not null),
            Has.All.Matches<DemoScenario>(s => tickets.Single(t => t.Scenario == s.Number).Body.Contains(s.ErrorCode!, StringComparison.Ordinal)));
    }

    private async Task<ServiceProvider> BootstrappedAsync(FakeZammadServer zammad)
    {
        ServiceProvider services = zammad.CreateServices();
        await new ZammadBootstrapper(
                services.GetRequiredService<ZammadClient>(), new UserSecretsStore(_secretsPath),
                new BootstrapSettings(new Uri("http://172.24.176.1:5401/webhooks/zammad"), _data.Noise.Operators, TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10)),
                TextWriter.Null, TimeProvider.System)
            .RunAsync(rotateToken: false, CancellationToken);

        return services;
    }

    private ZammadSeeder Seeder(ServiceProvider services, TicketDbContext db) =>
        new(services.GetRequiredService<ZammadClient>(), db, _data.Noise.Operators, TextWriter.Null);

    private TicketDbContext Open() => TicketDatabaseInitializer.Create(_connectionString, Dimensions);
}
