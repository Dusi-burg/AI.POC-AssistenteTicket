using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.DbInit;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Data.Tests;

/// <summary>Modello EF senza database: la dimensione del vettore è un parametro del modello, non una costante.</summary>
public class TicketDbContextModelTests
{
    private const string NoServer = "Server=(localdb)\\nessuno;Database=Ticket_Model";

    [Test]
    public void Model_TwoDimensionsInTheSameProcess_GiveTwoColumnTypes()
    {
        //SETUP
        using TicketDbContext small = TicketDatabaseInitializer.Create(NoServer, embeddingDimensions: 4);
        using TicketDbContext large = TicketDatabaseInitializer.Create(NoServer, embeddingDimensions: 768);

        //SUT
        string? smallType = EmbeddingColumnType(small);
        string? largeType = EmbeddingColumnType(large);

        Assert.That((smallType, largeType), Is.EqualTo(("vector(4)", "vector(768)")));
    }

    [Test]
    public void Model_Tables_AreSingularAndInTheirSchema()
    {
        //SETUP
        using TicketDbContext db = TicketDatabaseInitializer.Create(NoServer, embeddingDimensions: 4);

        //SUT
        var tables = db.Model.GetEntityTypes().Select(e => (Schema: e.GetSchema(), Table: e.GetTableName()!)).ToList();

        Assert.That(tables.Select(t => t.Schema), Is.All.AnyOf(TicketDbContext.SupportSchema, TicketDbContext.RagSchema, TicketDbContext.AgentSchema));
        Assert.That(tables.Select(t => t.Table), Has.None.EndsWith("s"), "nomi al singolare");
        Assert.That(tables.Select(t => t.Table), Has.None.Contains("Lookup"), "le lookup hanno il nome dell'enum");
    }

    private static string? EmbeddingColumnType(TicketDbContext db) =>
        db.Model.FindEntityType(typeof(Chunk))!.FindProperty(nameof(Chunk.Embedding))!.GetColumnType();
}
