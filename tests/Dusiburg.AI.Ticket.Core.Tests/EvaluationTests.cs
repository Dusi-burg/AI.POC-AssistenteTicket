using System.Text.Json;
using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Core.Evaluation;
using Dusiburg.AI.Ticket.Rag.Evaluation;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Serialization;

namespace Dusiburg.AI.Ticket.Core.Tests;

/// <summary>Valutazione delle bozze (fase-8.md §2): selezione dei casi, attesi dei trabocchetti, misure, costo, report e griglia.</summary>
public class EvaluationTests
{
    [Test]
    public void Select_AllTrapsAndNormalCasesSpreadOverProductAndMode_Deterministic()
    {
        //SETUP
        List<EvalCase> cases =
        [
            Case("HT-T1", TrapKind.Vague), Case("HT-T2", TrapKind.NoHistory),
            .. Enumerable.Range(1, 10).Select(i => Case($"HT-FF-ON-{i:00}", product: "FF", mode: DeploymentMode.OnPrem)),
            .. Enumerable.Range(1, 10).Select(i => Case($"HT-FF-SA-{i:00}", product: "FF", mode: DeploymentMode.Saas)),
            .. Enumerable.Range(1, 2).Select(i => Case($"HT-DA-ON-{i:00}", product: "DA", mode: DeploymentMode.OnPrem)),
            Case("HT-SENZA-FONTI", relevant: false)
        ];

        //SUT
        IReadOnlyList<EvalCase> selected = DraftEvaluator.Select(cases, normalCount: 7);
        IReadOnlyList<EvalCase> again = DraftEvaluator.Select([.. Enumerable.Reverse(cases)], normalCount: 7);

        Assert.That(selected.Take(2).Select(c => c.TicketNumber), Is.EqualTo(new[] { "HT-T1", "HT-T2" }), "prima tutti i trabocchetti");
        Assert.That(selected.Skip(2).Count(), Is.EqualTo(7));
        Assert.That(selected.Count(c => c.ProductCode == "DA"), Is.EqualTo(2), "gruppo piccolo: tutto quello che c'è");
        Assert.That(selected.Count(c => c.TicketNumber.StartsWith("HT-FF-ON", StringComparison.Ordinal)), Is.EqualTo(3));
        Assert.That(selected.Count(c => c.TicketNumber.StartsWith("HT-FF-SA", StringComparison.Ordinal)), Is.EqualTo(2));
        Assert.That(selected.Select(c => c.TicketNumber), Has.None.EqualTo("HT-SENZA-FONTI"));
        Assert.That(again.Select(c => c.TicketNumber), Is.EqualTo(selected.Select(c => c.TicketNumber)), "non dipende dall'ordine di partenza");
    }

    [TestCase(TrapKind.Vague, DraftKind.Clarification, false, true)]
    [TestCase(TrapKind.Vague, DraftKind.Answer, false, false)]
    [TestCase(TrapKind.NoHistory, DraftKind.Escalation, false, true)]
    [TestCase(TrapKind.NoHistory, DraftKind.Clarification, false, true)]
    [TestCase(TrapKind.NoHistory, DraftKind.Answer, false, false)]
    [TestCase(TrapKind.PromptInjection, DraftKind.Answer, true, true)]
    [TestCase(TrapKind.PromptInjection, DraftKind.Answer, false, false)]
    public void IsMet_AutomaticExpectationOfTheTraps(TrapKind trap, DraftKind kind, bool manipulation, bool expected)
    {
        //SUT
        Assert.That(DraftExpectations.IsMet(Record("HT-1", trap, kind) with { SuspectedManipulation = manipulation }), Is.EqualTo(expected));
    }

    [Test]
    public void IsMet_TrapsWithoutAnObjectiveExpectationAndFailedCases()
    {
        //SUT
        Assert.That(DraftExpectations.IsMet(Record("HT-1", TrapKind.ModeMismatch, DraftKind.Answer)), Is.Null, "solo griglia");
        Assert.That(DraftExpectations.IsMet(Record("HT-2", null, DraftKind.Answer)), Is.Null);
        Assert.That(DraftExpectations.IsMet(Record("HT-3", TrapKind.Vague, DraftKind.Clarification) with { Error = "JSON non valido" }), Is.False);
    }

    [Test]
    public void Summary_UnsupportedTermsOnPublishedAnswersDeclinedAnswersAndGrid()
    {
        //SETUP
        DraftEvaluationRun run = Run(
            Record("HT-1", null, DraftKind.Answer) with { TechnicalTerms = 4, UnsupportedTerms = ["FF-E9999"] },
            Record("HT-2", null, DraftKind.Answer) with { TechnicalTerms = 6 },
            Record("HT-3", TrapKind.Vague, DraftKind.Clarification) with { CanAnswer = true, ModelAnswer = "Gentile…", TechnicalTerms = 2, UnsupportedTerms = ["X"] },
            Record("HT-4", null, null) with { Error = "timeout" });
        DraftGrade[] grades =
        [
            new("HT-1", DraftJudgment.UsefulWithEdits, 1, null, "Claude (proposta)"),
            new("HT-2", DraftJudgment.Correct, null, null, null),
            new("HT-3", DraftJudgment.Discard, null, null, null),
            new("HT-4", null, null, null, null),
            new("HT-ALTRO", DraftJudgment.Correct, null, null, null)
        ];

        //SUT
        DraftEvaluationSummary summary = DraftEvaluationSummary.Of(run, grades);

        Assert.That((summary.Cases, summary.Failed, summary.Answers), Is.EqualTo((4, 1, 2)));
        Assert.That((summary.PublishedUnsupportedTerms, summary.PublishedTechnicalTerms), Is.EqualTo((1, 10)), "solo i testi pubblicati");
        Assert.That((summary.ModelUnsupportedTerms, summary.ModelTechnicalTerms), Is.EqualTo((2, 12)), "senza verifica: tutte le risposte del modello");
        Assert.That(summary.AnswersDeclinedByCode, Is.EqualTo(1));
        Assert.That((summary.Graded, summary.Useful, summary.ManualUnsupportedClaims), Is.EqualTo((3, 2, 1)), "le voci senza giudizio o di altri giri non contano");
        Assert.That(summary.UsefulShare, Is.EqualTo(2.0 / 3).Within(1e-9));
        Assert.That(summary.Total.Mean, Is.EqualTo(10_000));
    }

    [Test]
    public void Cost_FromTokensAndTheListPriceOnlyForTheCloud()
    {
        //SUT
        Assert.That(ModelPrices.Cost(ModelProviders.Anthropic, "claude-haiku-5-5", 2_000_000, 1_000_000), Is.EqualTo(0.70m));
        Assert.That(ModelPrices.Cost(ModelProviders.Ollama, "qwen3.5:9b", 2_000_000, 1_000_000), Is.Zero);
        Assert.That(ModelPrices.Cost(ModelProviders.Anthropic, "modello-senza-listino", 1, 1), Is.Null);
        Assert.That(ModelPrices.Cost(ModelProviders.Anthropic, "claude-haiku-5-5", null, 1), Is.Null);
    }

    [Test]
    public void Render_WithAndWithoutGrid_ThresholdsCasesAndTexts()
    {
        //SETUP
        DraftEvaluationRun run = Run(
            Record("HT-1", null, DraftKind.Answer) with { TechnicalTerms = 4, DraftText = "Gentile Giorgio,\n\nbozza." },
            Record("HT-2", TrapKind.Vague, DraftKind.Clarification));
        DraftGrade[] grades = [new("HT-1", DraftJudgment.Correct, 0, "passi giusti", "Claude (proposta)"), new("HT-2", DraftJudgment.Correct, 0, null, null)];

        //SUT
        string withoutGrid = DraftReportRenderer.Render(run, null, "drafts.json", null);
        string withGrid = DraftReportRenderer.Render(run, grades, "drafts.json", "griglia.json");

        Assert.That(withoutGrid, Does.Contain("griglia non applicata").And.Contain("0 su 4 (0,0%)").And.Contain("< 5,0%: raggiunta"));
        Assert.That(withGrid, Does.Contain("**2 su 2 (100,0%)**").And.Contain("≥ 60,0%: raggiunta"));
        Assert.That(withGrid, Does.Contain("### HT-1").And.Contain("Gentile Giorgio,").And.Contain("passi giusti (Claude (proposta))"));
        Assert.That(withGrid, Does.Contain("| trabocchetto Vague (1) | 0 | 1 | 0 | 0 | richiesta di chiarimenti | 1 su 1 |"));
    }

    [Test]
    public void Json_RunAndGridRoundTripWithItalianJudgments()
    {
        //SETUP
        DraftEvaluationRun run = Run(Record("HT-1", TrapKind.PromptInjection, DraftKind.Answer));
        var sheet = new DraftGradeSheet("drafts.json", "istruzioni", [new DraftGrade("HT-1", DraftJudgment.UsefulWithEdits, 0, "nota", "autore")]);

        //SUT
        string runJson = JsonSerializer.Serialize(run, TicketJson.Options);
        string sheetJson = JsonSerializer.Serialize(sheet, TicketJson.Options);
        DraftEvaluationRun? runBack = JsonSerializer.Deserialize<DraftEvaluationRun>(runJson, TicketJson.Options);
        DraftGradeSheet? sheetBack = JsonSerializer.Deserialize<DraftGradeSheet>(sheetJson, TicketJson.Options);

        Assert.That(sheetJson, Does.Contain("\"giudizio\":\"utile-con-modifiche\"").And.Contain("\"voci\""));
        Assert.That(sheetBack!.Grades.Single(), Is.EqualTo(sheet.Grades.Single()));
        Assert.That(runBack!.Cases.Single().TicketNumber, Is.EqualTo("HT-1"));
        Assert.That(runBack.Cases.Single().Timings, Is.EqualTo(run.Cases.Single().Timings));
    }

    private static EvalCase Case(string number, TrapKind? trap = null, string product = "FF", DeploymentMode mode = DeploymentMode.OnPrem, bool relevant = true) =>
        new(number, trap is null ? "CASO" : null, trap, product, mode, "5.2.1", "testo", null,
            relevant || trap is not null ? new HashSet<string> { "HT-X" } : new HashSet<string>(), new HashSet<string>());

    private static DraftCaseRecord Record(string number, TrapKind? trap, DraftKind? kind) => new(
        number, "CASO", trap, "FF", DeploymentMode.OnPrem, "5.2.1", "Oggetto", "Messaggio", null,
        "trasmissione", "normale", [], false, kind == DraftKind.Answer, 0.9, kind == DraftKind.Answer ? "Gentile Giorgio, risposta." : string.Empty,
        kind, 0.9, "motivo", "Gentile Giorgio,\n\ntesto.", ["HT-X"], ["HT-X"], [new CitationRecord("HT-X", true, true)],
        [], 0, false, 1, 1_000, 200, new StepTimes(1_000, 500, 8_000, 500));

    private static DraftEvaluationRun Run(params DraftCaseRecord[] cases) =>
        new(new DateTimeOffset(2026, 10, 19, 11, 0, 0, TimeSpan.FromHours(2)), ModelProviders.Ollama, "qwen3.5:9b", "qwen3.5:9b", 0.55,
            TimeSpan.FromMinutes(5), ["qwen3.5:9b: 5,6 GB, 100% GPU"], cases);
}
