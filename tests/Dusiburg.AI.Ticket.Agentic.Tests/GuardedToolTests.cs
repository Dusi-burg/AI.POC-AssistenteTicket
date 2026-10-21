using System.Text.Json;
using Dusiburg.AI.Ticket.Agentic.Tests.Support;
using Dusiburg.AI.Ticket.Core.Pipeline;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Knowledge;
using Microsoft.Extensions.AI;

namespace Dusiburg.AI.Ticket.Agentic.Tests;

/// <summary>La guardia dei tool della modalità agente: argomenti del codice, fonti leggibili, limite di chiamate, errori.</summary>
public class GuardedToolTests
{
    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [Test]
    public void Guard_HiddenArguments_AreNotInTheSchemaOfferedToTheModel()
    {
        //SETUP
        var tools = new FakeToolSet();

        //SUT
        List<AITool> guarded = ToolAgentDrafter.Guard(tools.All, new ToolAgentRun(6), TestTickets.Ferretti());

        JsonElement search = guarded.OfType<AIFunction>().Single(t => t.Name == TicketToolNames.SearchSources).JsonSchema;
        Assert.That(search.GetProperty("properties").EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "testo" }));
        Assert.That(search.GetProperty("required").EnumerateArray().Select(p => p.GetString()), Is.EqualTo(new[] { "testo" }));
    }

    [Test]
    public async Task SearchSources_InjectsTheCustomerFiltersEvenIfTheModelPassesOthers()
    {
        //SETUP
        var tools = new FakeToolSet();
        var run = new ToolAgentRun(6);
        AIFunction search = Tool(tools, run, TicketToolNames.SearchSources);

        //SUT
        await search.InvokeAsync(new AIFunctionArguments { ["testo"] = "FF-E2041", ["prodotto"] = "DA", ["versione"] = "1.0" }, CancellationToken);

        Assert.That(tools.Search.Received.Single(), Is.EquivalentTo(new Dictionary<string, string?>
        {
            ["testo"] = "FF-E2041", ["prodotto"] = "FF", ["modalita"] = "OnPrem", ["versione"] = "5.1.2"
        }));
        Assert.That(run.IsAllowed("HT-2025-0001"), Is.True);
        Assert.That(run.Calls.Single().Outcome, Is.EqualTo("ok"));
    }

    [Test]
    public async Task ReadSource_KeyNotFromAResultOfThisRun_IsRejectedBeforeTheServer()
    {
        //SETUP
        var tools = new FakeToolSet();
        var run = new ToolAgentRun(6);
        AIFunction read = Tool(tools, run, TicketToolNames.ReadSource);

        //SUT
        object? result = await read.InvokeAsync(new AIFunctionArguments { ["chiave"] = "HT-2020-0001" }, CancellationToken);

        Assert.That(tools.Read.Received, Is.Empty);
        Assert.That(ToolResultReader.Read(result).ErrorCode, Is.EqualTo(GuardedTool.NotAllowed));
        Assert.That(run.Calls.Single().Outcome, Is.EqualTo("rejected:NOT_ALLOWED"));
        Assert.That(run.Evidence, Is.Empty);
    }

    [Test]
    public async Task ReadSource_AfterASearch_ReadsTheWholeSourceAndKeepsTheSearchDistance()
    {
        //SETUP
        var tools = new FakeToolSet();
        var run = new ToolAgentRun(6);
        await Tool(tools, run, TicketToolNames.SearchSources).InvokeAsync(new AIFunctionArguments { ["testo"] = "TLS" }, CancellationToken);

        //SUT
        await Tool(tools, run, TicketToolNames.ReadSource).InvokeAsync(new AIFunctionArguments { ["chiave"] = "HT-2025-0001" }, CancellationToken);

        SourceEvidence evidence = run.Evidence.Single();
        Assert.That((evidence.Key, evidence.Whole), Is.EqualTo(("HT-2025-0001", true)));
        Assert.That(evidence.Chunks, Is.EqualTo(new[] { new EvidenceChunk(11, 0.18) }), "dalla ricerca solo il chunk migliore, con la distanza della fonte");
    }

    [Test]
    public async Task CustomerHistoryAndZammadSearch_MakeTheirKeysReadableWithoutCountingAsRead()
    {
        //SETUP
        var tools = new FakeToolSet();
        var run = new ToolAgentRun(6);

        //SUT
        await Tool(tools, run, TicketToolNames.CustomerHistory).InvokeAsync(new AIFunctionArguments(), CancellationToken);
        await Tool(tools, run, TicketToolNames.SearchZammad).InvokeAsync(new AIFunctionArguments { ["testo"] = "TLS" }, CancellationToken);

        Assert.That(tools.History.Received.Single()["email"], Is.EqualTo("giorgio.ferretti@ferretti-contabilita.example.it"));
        Assert.That(tools.Zammad.Received.Single()["escludiNumero"], Is.EqualTo("71170"));
        Assert.That((run.IsAllowed("HT-2024-0100"), run.IsAllowed("HT-2024-0200")), Is.EqualTo((true, true)));
        Assert.That(run.Evidence, Is.Empty);
    }

    [Test]
    public async Task ReleaseNotes_AreReadWhole()
    {
        //SETUP
        var tools = new FakeToolSet();
        var run = new ToolAgentRun(6);

        //SUT
        await Tool(tools, run, TicketToolNames.ReleaseNotes).InvokeAsync(new AIFunctionArguments { ["versioneDa"] = "5.1.2" }, CancellationToken);

        Assert.That(tools.Notes.Received.Single()["prodotto"], Is.EqualTo("FF"));
        SourceEvidence note = run.Evidence.Single();
        Assert.That((note.Source, note.Key, note.IsWhole), Is.EqualTo((ChunkSource.ReleaseNote, "RN-FF-5.2.0-03", true)));
    }

    [Test]
    public async Task Limit_CallsBeyondTheMaximum_AreRejectedAndDoNotReachTheServer()
    {
        //SETUP
        var tools = new FakeToolSet();
        var run = new ToolAgentRun(2);
        AIFunction search = Tool(tools, run, TicketToolNames.SearchSources);

        //SUT
        for (int i = 0; i < 4; i++)
        {
            await search.InvokeAsync(new AIFunctionArguments { ["testo"] = $"prova {i}" }, CancellationToken);
        }

        Assert.That(tools.Search.Received, Has.Count.EqualTo(2));
        Assert.That(run.Calls.Select(c => c.Outcome), Is.EqualTo(new[] { "ok", "ok", "rejected:LIMIT_REACHED", "rejected:LIMIT_REACHED" }));
    }

    [Test]
    public async Task ToolThatThrows_BecomesAnErrorTheModelCanRead()
    {
        //SETUP
        var tools = new FakeToolSet();
        var run = new ToolAgentRun(6);
        var failing = new FakeTool(TicketToolNames.SearchSources, ["testo"], _ => throw new HttpRequestException("connessione rifiutata"));
        AIFunction search = ToolAgentDrafter.Guard([failing, .. tools.All.Where(t => t.Name != TicketToolNames.SearchSources)], run, TestTickets.Ferretti())
            .OfType<AIFunction>().Single(t => t.Name == TicketToolNames.SearchSources);

        //SUT
        object? result = await search.InvokeAsync(new AIFunctionArguments { ["testo"] = "TLS" }, CancellationToken);

        Assert.That(ToolResultReader.Read(result).ErrorCode, Is.EqualTo("UPSTREAM_UNAVAILABLE"));
        Assert.That(run.Calls.Single().Outcome, Is.EqualTo("error:UPSTREAM_UNAVAILABLE"));
    }

    [Test]
    public void Guard_ServerWithoutATool_TheAgentDoesNotStart()
    {
        //SETUP
        var tools = new FakeToolSet();

        //SUT
        Assert.That(() => ToolAgentDrafter.Guard([.. tools.All.Where(t => t.Name != TicketToolNames.ReleaseNotes)], new ToolAgentRun(6), TestTickets.Ferretti()),
            Throws.InvalidOperationException.With.Message.Contains(TicketToolNames.ReleaseNotes));
    }

    private static AIFunction Tool(FakeToolSet tools, ToolAgentRun run, string name) =>
        ToolAgentDrafter.Guard(tools.All, run, TestTickets.Ferretti()).OfType<AIFunction>().Single(t => t.Name == name);
}
