using Dusiburg.AI.Ticket.Core.Context;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Knowledge;
using Microsoft.Extensions.Configuration;

namespace Dusiburg.AI.Ticket.Core.Tests;

/// <summary>Modalità agente (Fase 9b): manopola <c>PIPELINE_MODE</c> e prompt delle due fasi, con le stesse regole della bozza fissa.</summary>
public class ToolAgentPromptTests
{
    private static readonly ModelClassification Classification = new("trasmissione", "alta", "it", [], false);

    [TestCase(null, PipelineMode.Fixed)]
    [TestCase("fixed", PipelineMode.Fixed)]
    [TestCase(" Agent ", PipelineMode.Agent)]
    public void PipelineMode_FromConfiguration(string? value, PipelineMode expected)
    {
        //SUT
        AgentOptions options = AgentOptions.FromConfiguration(Configuration(value));

        Assert.That(options.Mode, Is.EqualTo(expected));
    }

    [TestCase("agente")]
    [TestCase("3")]
    public void PipelineMode_UnknownValue_IsAConfigurationError(string value)
    {
        //SUT
        Assert.That(() => AgentOptions.FromConfiguration(Configuration(value)),
            Throws.InvalidOperationException.With.Message.Contains("PIPELINE_MODE").And.Message.Contains("fixed oppure agent"));
    }

    [Test]
    public void ToolAgentWork_SameRulesAsTheFixedDraftToolsAndLimit()
    {
        //SETUP
        IncomingTicket ticket = Ticket();

        //SUT
        (string instructions, string task) = PromptBuilder.ToolAgentWork(ticket, Classification, maxToolCalls: 6);

        string fixedSystem = PromptBuilder.Draft(ticket, Classification, [])[0].Text;
        Assert.That(Rules(instructions), Is.EqualTo(Rules(fixedSystem)));
        Assert.That(instructions, Does.Contain("al più 6 chiamate"));
        Assert.That(TicketToolNames.All, Has.All.Matches<string>(name => instructions.Contains(name, StringComparison.Ordinal)));
        Assert.That(instructions, Does.Not.Contain("Rispondi solo con il JSON richiesto"), "nella fase di lavoro niente JSON");
        Assert.That(task, Does.Contain("<<<TICKET>>>").And.Contain("CONTESTO DEL CLIENTE").And.Contain("categoria trasmissione"));
    }

    [Test]
    public void ToolAgentOutcome_ListsTheReadKeysOrSaysThereAreNone()
    {
        //SUT
        string withKeys = PromptBuilder.ToolAgentOutcome(["HT-2025-0001", "KB-FF-0004"]);
        string without = PromptBuilder.ToolAgentOutcome([]);

        Assert.That(withKeys, Does.Contain("HT-2025-0001, KB-FF-0004"));
        Assert.That(without, Does.Contain("\"puoRispondere\" è false"));
    }

    /// <summary>Dalle regole all'esempio di stile compreso: il blocco che le due modalità condividono.</summary>
    private static string Rules(string prompt)
    {
        int start = prompt.IndexOf("Regole obbligatorie:", StringComparison.Ordinal);
        int end = prompt.IndexOf("Cordiali saluti,\"", start, StringComparison.Ordinal);

        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        Assert.That(end, Is.GreaterThan(start));

        return prompt[start..end];
    }

    private static IConfiguration Configuration(string? mode) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [AgentOptions.Keys.PipelineMode] = mode }).Build();

    private static IncomingTicket Ticket() => new(170, "71170", "Fatture non trasmesse: errore FF-E2041", 429,
        [new ThreadMessage(429, MessageAuthor.Customer, DateTimeOffset.Parse("2026-10-16T09:00:00Z"), "Da stamattina errore FF-E2041.")],
        new CustomerContext("Studio Contabile Ferretti S.r.l.", "Giorgio Ferretti", "giorgio.ferretti@ferretti-contabilita.example.it", "FF", "5.1.2",
            DeploymentMode.OnPrem, SlaLevel.Plus, "Zammad"));
}
