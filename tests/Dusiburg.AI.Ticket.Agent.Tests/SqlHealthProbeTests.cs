using Dusiburg.AI.Ticket.Agent.Health;
using Dusiburg.AI.Ticket.Shared.Health;
using Microsoft.Extensions.Configuration;

namespace Dusiburg.AI.Ticket.Agent.Tests;

/// <summary>
/// Controlli SQL veri su <c>(localdb)\localdev</c>, senza creare database: il database applicativo arriva con <c>DbInit</c>
/// (Fase 2), qui si usa <c>master</c> come database esistente.
/// </summary>
public class SqlHealthProbeTests
{
    /// <summary>Il controllo 3 usa il tipo <c>VECTOR</c>: serve LocalDB 17 (SQL Server 2025), che la CI può non avere.</summary>
    private const string RequiresSql2025 = "RequiresSql2025";

    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [Test]
    [Category(RequiresSql2025)]
    public async Task RunAsync_ExistingDatabase_AllChecksOk()
    {
        //SETUP
        var probe = new SqlHealthProbe(Configuration(LocalConnectionString("master")));

        //SUT
        IReadOnlyList<ProbeResult> results = await probe.RunAsync(CancellationToken);

        Assert.That(results.Select(r => r.Number), Is.EqualTo(new[] { 1, 2, 3, 4 }));
        Assert.That(results.Select(r => r.Status), Is.All.EqualTo(ProbeStatus.Ok), string.Join(" | ", results.Select(r => r.Detail)));
    }

    [Test]
    public async Task RunAsync_DatabaseNotCreatedYet_WarningPointingToDbInit()
    {
        //SETUP
        var probe = new SqlHealthProbe(Configuration(LocalConnectionString("Ticket_Test_Inesistente")));

        //SUT
        IReadOnlyList<ProbeResult> results = await probe.RunAsync(CancellationToken);

        ProbeResult database = results.Single(r => r.Number == 4);
        Assert.That(results.Single(r => r.Number == 1).Status, Is.EqualTo(ProbeStatus.Ok));
        Assert.That(database.Status, Is.EqualTo(ProbeStatus.Warning));
        Assert.That(database.Detail, Does.Contain("DbInit"));
    }

    [Test]
    public async Task RunAsync_ConnectionStringMissing_ErrorAndOtherChecksSkipped()
    {
        //SETUP
        var probe = new SqlHealthProbe(new ConfigurationBuilder().Build());

        //SUT
        IReadOnlyList<ProbeResult> results = await probe.RunAsync(CancellationToken);

        Assert.That(results.Select(r => r.Number), Is.EqualTo(new[] { 1, 2, 3, 4 }));
        Assert.That(results.Select(r => r.Status), Is.All.EqualTo(ProbeStatus.Error));
        Assert.That(results[0].Detail, Does.Contain("ConnectionStrings:sql"));
    }

    private static string LocalConnectionString(string database) =>
        $@"Server=(localdb)\localdev;Database={database};Trusted_Connection=True;TrustServerCertificate=True";

    private static IConfiguration Configuration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:sql"] = connectionString })
            .Build();
}
