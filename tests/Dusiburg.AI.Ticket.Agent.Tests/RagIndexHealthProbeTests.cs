using Dusiburg.AI.Ticket.Agent.Health;
using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.DbInit;
using Dusiburg.AI.Ticket.Shared.Health;
using Microsoft.Extensions.Configuration;

namespace Dusiburg.AI.Ticket.Agent.Tests;

/// <summary>Controllo 13 "Indice RAG": esiti per stato dell'indice, più la lettura da un database creato da <c>DbInit</c>.</summary>
public class RagIndexHealthProbeTests
{
    private static readonly DateTimeOffset UpdatedAt = new(2026, 10, 16, 15, 0, 0, TimeSpan.Zero);

    [Test]
    public void Evaluate_CompleteIndexOfTheConfiguredModel_Ok()
    {
        //SETUP
        RagIndexHealthProbe probe = Probe(dimensions: 768);

        //SUT
        (ProbeStatus status, string detail) = probe.Evaluate(new(845, 0, 664, 121, 60, "embeddinggemma", 768, UpdatedAt));

        Assert.That(status, Is.EqualTo(ProbeStatus.Ok));
        Assert.That(detail, Does.Contain("845 chunk (ticket 664, KB 121, note 60)").And.Contain("embeddinggemma (768)"));
    }

    [TestCase(0, 0, null, null, "indice vuoto")]
    [TestCase(845, 0, null, null, "senza EmbeddingInfo")]
    [TestCase(845, 0, "bge-m3", 768, "bge-m3")]
    [TestCase(845, 0, "embeddinggemma", 512, "(512)")]
    [TestCase(845, 12, "embeddinggemma", 768, "--solo-mancanti")]
    public void Evaluate_IncompleteOrStaleIndex_WarningWithTheCommandToRun(int chunks, int withoutVector, string? model, int? dimensions, string expected)
    {
        //SETUP
        RagIndexHealthProbe probe = Probe(dimensions: 768);

        //SUT
        (ProbeStatus status, string detail) = probe.Evaluate(new(chunks, withoutVector, chunks, 0, 0, model, dimensions, model is null ? null : UpdatedAt));

        Assert.That(status, Is.EqualTo(ProbeStatus.Warning));
        Assert.That(detail, Does.Contain(expected).And.Contain("Cli embed"));
    }

    [Test]
    [Category("RequiresSql2025")]
    public async Task RunAsync_DatabaseJustCreated_WarnsThatTheIndexIsEmpty()
    {
        //SETUP
        string connectionString = TicketDatabaseInitializer.LocalConnectionStringFor($"Ticket_Test_{Guid.NewGuid():N}");
        await TicketDatabaseInitializer.RecreateAsync(connectionString, 4, seed: false, TestContext.CurrentContext.CancellationToken);

        try
        {
            RagIndexHealthProbe probe = Probe(dimensions: 4, connectionString);

            //SUT
            ProbeResult result = (await probe.RunAsync(TestContext.CurrentContext.CancellationToken)).Single();

            Assert.That(result.Number, Is.EqualTo(RagIndexHealthProbe.Number));
            Assert.That((result.Status, result.Detail), Is.EqualTo((ProbeStatus.Warning, "indice vuoto: eseguire 'Cli embed'")));
        }
        finally
        {
            await TicketDatabaseInitializer.DropAsync(connectionString);
        }
    }

    [Test]
    [Category("RequiresSql2025")]
    public async Task RunAsync_MissingDatabase_WarnsToRunDbInit()
    {
        //SETUP
        RagIndexHealthProbe probe = Probe(dimensions: 4, TicketDatabaseInitializer.LocalConnectionStringFor($"Ticket_Missing_{Guid.NewGuid():N}"));

        //SUT
        ProbeResult result = (await probe.RunAsync(TestContext.CurrentContext.CancellationToken)).Single();

        Assert.That(result.Status, Is.EqualTo(ProbeStatus.Warning));
        Assert.That(result.Detail, Does.Contain("DbInit"));
    }

    private static RagIndexHealthProbe Probe(int dimensions, string connectionString = "Server=(localdb)\\localdev;Database=Ticket")
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:sql"] = connectionString,
                [AiOptions.Keys.EmbeddingDimensions] = dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture)
            })
            .Build();

        return new RagIndexHealthProbe(configuration, AiOptions.FromConfiguration(configuration));
    }
}
