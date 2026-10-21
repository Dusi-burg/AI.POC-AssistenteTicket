using Dusiburg.AI.Ticket.Agentic.Tests.Support;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Core.Pipeline;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dusiburg.AI.Ticket.Agentic.Tests;

/// <summary>
/// L'agente per intero con un modello finto che chiama i tool (G12): fase di lavoro con i tool dietro la guardia, poi la bozza in
/// JSON senza tool, con le sole fonti lette ammesse dallo schema.
/// </summary>
public class ToolAgentDrafterTests
{
    private const string Classification = """{"categoria":"trasmissione","priorita":"alta","lingua":"it","informazioniMancanti":[],"sospettaManipolazione":false}""";

    private const string Draft = """{"puoRispondere":true,"autovalutazione":0.9,"risposta":"Gentile Giorgio,\n\nimporti la catena di certificati.\n\nCordiali saluti,","fonti":[{"id":"HT-2025-0001","motivo":"stesso errore"}],"domandePerIlCliente":[]}""";

    private static CancellationToken CancellationToken => TestContext.CurrentContext.CancellationToken;

    [Test]
    public async Task Draft_SearchThenReadThenJson_ReturnsDraftEvidenceAndCalls()
    {
        //SETUP
        var tools = new FakeToolSet();
        var models = new ScriptedAgentModels(
            ScriptedAgentModels.Call("c1", "cerca_fonti", new { testo = "FF-E2041 handshake TLS" }),
            ScriptedAgentModels.Call("c2", "leggi_fonte", new { chiave = "HT-2025-0001" }),
            ScriptedAgentModels.Text("Ho trovato HT-2025-0001: stesso errore, catena di certificati."),
            ScriptedAgentModels.Text(Draft));

        //SUT
        ToolAgentDraft result = await Drafter(models, tools).DraftAsync(TestTickets.Ferretti(), Classify(), CancellationToken);

        Assert.That(result.Draft.Value.Sources.Single().Id, Is.EqualTo("HT-2025-0001"));
        Assert.That((result.Draft.ModelId, result.Draft.Attempts, result.Draft.InputTokens), Is.EqualTo(("stub-agent", 1, (long?)400)));
        Assert.That(result.ToolCalls.Select(c => (c.ToolName, c.Outcome)), Is.EqualTo(new[] { ("cerca_fonti", "ok"), ("leggi_fonte", "ok") }));
        Assert.That(result.Evidence.Single().Key, Is.EqualTo("HT-2025-0001"));

        (IReadOnlyList<ChatMessage> work, ChatOptions? workOptions) = models.Calls[0];
        Assert.That(workOptions!.Tools, Has.Count.EqualTo(5));
        Assert.That(workOptions.Instructions, Does.Contain("cerca_fonti").And.Contain("Regole obbligatorie"));
        Assert.That(work.Last().Text, Does.Contain("<<<TICKET>>>"));

        (IReadOnlyList<ChatMessage> outcome, ChatOptions? outcomeOptions) = models.Calls[^1];
        Assert.That(outcomeOptions!.Tools, Is.Null.Or.Empty, "la bozza si scrive senza tool");
        Assert.That(((ChatResponseFormatJson)outcomeOptions.ResponseFormat!).Schema!.Value.GetRawText(), Does.Contain("\"enum\":[\"HT-2025-0001\"]"));
        Assert.That(outcome.SelectMany(m => m.Contents), Has.None.InstanceOf<FunctionCallContent>(), "la conversazione con i tool diventa testo");
        Assert.That(string.Join("\n", outcome.Select(m => m.Text)), Does.Contain("[chiamata cerca_fonti").And.Contain("[risultato di leggi_fonte]"));
        Assert.That(outcome.Last().Text, Does.Contain("HT-2025-0001"));
    }

    [Test]
    public async Task Draft_ModelAnswersWithoutTools_NoEvidenceAndNoCitableSource()
    {
        //SETUP
        var models = new ScriptedAgentModels(
            ScriptedAgentModels.Text("So già la risposta."),
            ScriptedAgentModels.Text("""{"puoRispondere":true,"autovalutazione":0.9,"risposta":"Gentile Giorgio,","fonti":[],"domandePerIlCliente":[]}"""));

        //SUT
        ToolAgentDraft result = await Drafter(models, new FakeToolSet()).DraftAsync(TestTickets.Ferretti(), Classify(), CancellationToken);

        Assert.That((result.ToolCalls.Count, result.Evidence.Count), Is.EqualTo((0, 0)));
        Assert.That(((ChatResponseFormatJson)models.Calls[^1].Options!.ResponseFormat!).Schema!.Value.GetRawText(), Does.Contain("\"maxItems\":0"));
    }

    [Test]
    public async Task Draft_InvalidJsonTwice_IsAnInvalidModelOutput()
    {
        //SETUP
        var models = new ScriptedAgentModels(ScriptedAgentModels.Text("fatto"), ScriptedAgentModels.Text("{"), ScriptedAgentModels.Text("no"));

        //SUT
        Assert.That(async () => await Drafter(models, new FakeToolSet()).DraftAsync(TestTickets.Ferretti(), Classify(), CancellationToken),
            Throws.TypeOf<InvalidModelOutputException>());
    }

    [Test]
    public void Merge_ConsecutiveMessagesOfTheSameRole_BecomeOne()
    {
        //SUT
        List<ChatMessage> merged = ToolAgentDrafter.Merge(
        [
            new(ChatRole.System, "s"), new(ChatRole.User, "a"), new(ChatRole.User, "b"), new(ChatRole.Assistant, "c"), new(ChatRole.User, "d")
        ]);

        Assert.That(merged.Select(m => (m.Role.Value, m.Text)), Is.EqualTo(new[] { ("system", "s"), ("user", "a\n\nb"), ("assistant", "c"), ("user", "d") }));
    }

    private static ToolAgentDrafter Drafter(ScriptedAgentModels models, FakeToolSet tools) =>
        new(models, tools, new ToolAgentOptions(new Uri(ToolAgentOptions.LocalMcpUrl), "key", 6), NullLoggerFactory.Instance);

    private static ModelClassification Classify() => ModelJson.Parse<ModelClassification>(Classification).Value!;
}
