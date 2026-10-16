using Dusiburg.AI.Ticket.Agent.Health;
using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.DbInit;
using Dusiburg.AI.Ticket.Shared.Health;
using Microsoft.Extensions.Configuration;

namespace Dusiburg.AI.Ticket.Agent.Tests;

/// <summary>
/// Controllo 4 su un database creato da <c>DbInit</c> con <c>VECTOR(4)</c>: la colonna deve avere la dimensione di
/// <c>EMBEDDING_DIMENSIONS</c>. Il database si crea solo con LocalDB 17 (SQL Server 2025), che la CI può non avere.
/// </summary>
[Category("RequiresSql2025")]
public class SqlHealthProbeDatabaseTests
{
    private const int Dimensions = 4;

    private string _connectionString = null!;

    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [OneTimeSetUp]
    public async Task CreateDatabase()
    {
        _connectionString = TicketDatabaseInitializer.LocalConnectionStringFor($"Ticket_Test_{Guid.NewGuid():N}");
        await TicketDatabaseInitializer.RecreateAsync(_connectionString, Dimensions, seed: false, CancellationToken);
    }

    [OneTimeTearDown]
    public async Task DropDatabase() => await TicketDatabaseInitializer.DropAsync(_connectionString);

    [Test]
    public async Task RunAsync_DimensionsMatchConfiguration_AllChecksOk()
    {
        //SETUP
        var probe = new SqlHealthProbe(Configuration(Dimensions));

        //SUT
        IReadOnlyList<ProbeResult> results = await probe.RunAsync(CancellationToken);

        Assert.That(results.Select(r => r.Status), Is.All.EqualTo(ProbeStatus.Ok), string.Join(" | ", results.Select(r => r.Detail)));
        Assert.That(results[3].Detail, Does.Contain("VECTOR(4)"));
    }

    [Test]
    public async Task RunAsync_DimensionsDifferFromConfiguration_WarningAskingToRecreate()
    {
        //SETUP
        var probe = new SqlHealthProbe(Configuration(768));

        //SUT
        IReadOnlyList<ProbeResult> results = await probe.RunAsync(CancellationToken);

        ProbeResult database = results.Single(r => r.Number == 4);
        Assert.That(database.Status, Is.EqualTo(ProbeStatus.Warning));
        Assert.That(database.Detail, Does.Contain("VECTOR(4)").And.Contain("768").And.Contain("DbInit"));
    }

    private IConfiguration Configuration(int embeddingDimensions) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:sql"] = _connectionString,
                [AiOptions.Keys.EmbeddingDimensions] = embeddingDimensions.ToString(System.Globalization.CultureInfo.InvariantCulture)
            })
            .Build();
}
