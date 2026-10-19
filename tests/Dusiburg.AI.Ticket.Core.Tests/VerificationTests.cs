using Dusiburg.AI.Ticket.Core.Tests.Support;
using Dusiburg.AI.Ticket.Core.Verification;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Core.Tests;

/// <summary>Verifica a valle (citazioni, termini tecnici, firma) e confidenza con la decisione del codice (P6).</summary>
public class VerificationTests
{
    [Test]
    public void Verify_CitationOfASourceNotGiven_IsNotVerified()
    {
        //SETUP
        var draft = TestData.Draft(cited: ["KB-FF-0004", "KB-FF-9999"]);

        //SUT
        VerificationResult result = DraftVerifier.Verify(draft, TestData.Sources(), "ticket");

        Assert.That(result.Citations.Select(c => (c.Id, c.IsVerified)), Is.EqualTo(new[] { ("KB-FF-0004", true), ("KB-FF-9999", false) }));
        Assert.That(result.CitationRatio, Is.EqualTo(0.5));
    }

    [Test]
    public void Verify_TechnicalTermsAbsentFromSourcesAndTicket_AreUnsupported()
    {
        //SETUP
        var draft = TestData.Draft(
            "Gentile Giorgio,\n\nl'errore FF-E2041 si risolve dalla 5.1.2: aprire Impostazioni > Trasmissione, poi Impostazioni > Proxy, " +
            "lanciare ffadmin truststore import e ffadmin cache purge --all. Vale anche per FF-E9999.\n\nCordiali saluti,", "KB-FF-0004");

        //SUT
        VerificationResult result = DraftVerifier.Verify(draft, TestData.Sources(), "Errore FF-E2041 sulla 5.1.2");

        Assert.That(result.UnsupportedTerms, Is.EquivalentTo(new[] { "FF-E9999", "Impostazioni > Proxy", "ffadmin cache purge --all" }));
        Assert.That(result.TechnicalTerms, Is.EqualTo(7));
        Assert.That(result.SupportRatio, Is.EqualTo(4.0 / 7).Within(1e-9));
    }

    [Test]
    public void TechnicalTerms_MenuPathStartsAtTheMenuItemNotAtTheVerb()
    {
        //SUT
        string[] terms = [.. DraftVerifier.TechnicalTerms("1. Accedere a Impostazioni > Firma remota;\n2. Cliccare su Rinnova.")];

        Assert.That(terms, Is.EqualTo(new[] { "Impostazioni > Firma remota" }));
    }

    [TestCase("1. Verificare in Amministrazione > Calendari che il calendario non sia vuoto;", "Amministrazione > Calendari")]
    [TestCase("Aprire Impostazioni > Firma remota per il rinnovo.", "Impostazioni > Firma remota")]
    [TestCase("3. Eseguire il comando ffadmin receive --now per scaricare le fatture arretrate.", "ffadmin receive --now")]
    [TestCase("Verificare con ffadmin truststore verify --channel che restituisca OK.", "ffadmin truststore verify --channel")]
    [TestCase("Lanciare ffadmin truststore import <file .p7b> come amministratore.", "ffadmin truststore import <file .p7b>")]
    public void TechnicalTerms_TermsStopBeforeTheWordsThatFollow(string text, string expected)
    {
        //SUT
        string[] terms = [.. DraftVerifier.TechnicalTerms(text)];

        Assert.That(terms, Is.EqualTo(new[] { expected }), "falsi positivi della Fase 8: le parole dopo il termine non ne fanno parte");
    }

    [TestCase("Gentile Giorgio,\n\ntesto.\n\nCordiali saluti,\nSupporto Tecnico Dusiburg", "Gentile Giorgio,\n\ntesto.\n\nCordiali saluti,", true)]
    [TestCase("Gentile Giorgio,\n\ntesto.\n\nCordiali saluti,\nMarta Ferrari\nAssistenza clienti", "Gentile Giorgio,\n\ntesto.\n\nCordiali saluti,", true)]
    [TestCase("Gentile Giorgio,\n\ntesto.\n\nIl team di supporto Dusiburg", "Gentile Giorgio,\n\ntesto.", true)]
    [TestCase("Gentile Giorgio,\n\ntesto.\n\nCordiali saluti,", "Gentile Giorgio,\n\ntesto.\n\nCordiali saluti,", false)]
    public void RemoveSignature_NothingAfterTheClosing(string answer, string expected, bool removed)
    {
        //SUT
        (string text, bool wasRemoved) = DraftVerifier.RemoveSignature(answer);

        Assert.That((text, wasRemoved), Is.EqualTo((expected, removed)));
    }

    [TestCase(0.15, 1.0)]
    [TestCase(0.20, 1.0)]
    [TestCase(0.325, 0.5)]
    [TestCase(0.45, 0.0)]
    [TestCase(0.52, 0.0)]
    public void RetrievalScore_LinearBetweenGoodAndPoorDistance(double distance, double expected)
    {
        //SETUP
        RetrievedSource[] sources = [TestData.Source(1, "HT-1", ChunkSource.HistoricalTicket, "x", distance)];

        //SUT
        Assert.That(ConfidenceCalculator.RetrievalScore(sources), Is.EqualTo(expected).Within(1e-9));
        Assert.That(ConfidenceCalculator.RetrievalScore([]), Is.Zero);
    }

    [Test]
    public void Assess_CloseSourcesAndVerifiedCitations_Answer()
    {
        //SETUP
        VerificationResult verification = DraftVerifier.Verify(TestData.Draft(cited: "KB-FF-0004"), TestData.Sources(), "ticket");

        //SUT
        ConfidenceAssessment assessment = ConfidenceCalculator.Assess(TestData.Sources(), verification, missingInformation: 0, selfAssessment: 0.9, modelCanAnswer: true, threshold: 0.55);

        Assert.That(assessment.Kind, Is.EqualTo(DraftKind.Answer));
        Assert.That(assessment.Confidence, Is.EqualTo(0.985).Within(1e-9));
        Assert.That((assessment.Retrieval, assessment.Citation, assessment.Coverage), Is.EqualTo((1.0, 1.0, 1.0)));
    }

    [Test]
    public void Assess_BelowThreshold_ClarificationWhenInformationIsMissingEscalationWhenTheHistoryIsEmpty()
    {
        //SETUP
        RetrievedSource[] far = [TestData.Source(1, "RN-DA-3.2.0-02", ChunkSource.ReleaseNote, "x", 0.49)];
        RetrievedSource[] middle = [TestData.Source(1, "HT-1", ChunkSource.HistoricalTicket, "x", 0.33)];
        VerificationResult noCitations = DraftVerifier.Verify(TestData.Draft(cited: []), far, "ticket");

        //SUT
        ConfidenceAssessment noHistory = ConfidenceCalculator.Assess(far, noCitations, missingInformation: 0, selfAssessment: 0, modelCanAnswer: true, threshold: 0.55);
        ConfidenceAssessment vague = ConfidenceCalculator.Assess(middle, noCitations, missingInformation: 4, selfAssessment: 0.2, modelCanAnswer: true, threshold: 0.55);
        ConfidenceAssessment unclear = ConfidenceCalculator.Assess(middle, noCitations, missingInformation: 0, selfAssessment: 0.2, modelCanAnswer: true, threshold: 0.55);

        Assert.That(noHistory.Kind, Is.EqualTo(DraftKind.Escalation));
        Assert.That(noHistory.Confidence, Is.EqualTo(0.15).Within(1e-9));
        Assert.That(vague.Kind, Is.EqualTo(DraftKind.Clarification));
        Assert.That(vague.Coverage, Is.Zero);
        Assert.That(unclear.Kind, Is.EqualTo(DraftKind.Clarification), "storico vicino ma non abbastanza: si chiede, non si passa al secondo livello");
    }

    [Test]
    public void Assess_ModelCannotAnswer_NeverAnswerEvenAboveThreshold()
    {
        //SETUP
        VerificationResult verification = DraftVerifier.Verify(TestData.Draft(cited: "KB-FF-0004"), TestData.Sources(), "ticket");
        RetrievedSource[] far = [TestData.Source(1, "RN-DA-3.2.0-02", ChunkSource.ReleaseNote, "x", 0.49)];

        //SUT
        ConfidenceAssessment close = ConfidenceCalculator.Assess(TestData.Sources(), verification, missingInformation: 0, selfAssessment: 0.9, modelCanAnswer: false, threshold: 0.55);
        ConfidenceAssessment vague = ConfidenceCalculator.Assess(TestData.Sources(), verification, missingInformation: 2, selfAssessment: 0.9, modelCanAnswer: false, threshold: 0.55);
        ConfidenceAssessment noHistory = ConfidenceCalculator.Assess(far, verification, missingInformation: 0, selfAssessment: 0.9, modelCanAnswer: false, threshold: 0.55);

        Assert.That(close.Confidence, Is.GreaterThan(0.55));
        Assert.That(close.Kind, Is.EqualTo(DraftKind.Clarification));
        Assert.That(close.Reason, Does.StartWith("il modello non può rispondere"));
        Assert.That(vague.Kind, Is.EqualTo(DraftKind.Clarification));
        Assert.That(noHistory.Kind, Is.EqualTo(DraftKind.Escalation));
    }

    [Test]
    public void Assess_NoCitationsAtAll_CitationSignalIsZeroEvenWithoutTechnicalTerms()
    {
        //SETUP
        VerificationResult verification = DraftVerifier.Verify(TestData.Draft("Gentile Giorgio,\n\nverifichiamo.\n\nCordiali saluti,"), TestData.Sources(), "ticket");

        //SUT
        ConfidenceAssessment assessment = ConfidenceCalculator.Assess(TestData.Sources(), verification, 0, 0.9, true, 0.55);

        Assert.That(verification.SupportRatio, Is.EqualTo(1));
        Assert.That(assessment.Citation, Is.Zero);
    }
}
