using Dusiburg.AI.Ticket.Core.Composition;
using Dusiburg.AI.Ticket.Core.Context;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Core.Tests;

/// <summary>Testo per il cliente (P8: sempre con la dicitura AI), HTML per l'editor di Zammad, testo dagli articoli HTML.</summary>
public class CompositionTests
{
    [TestCase(DraftKind.Answer)]
    [TestCase(DraftKind.Clarification)]
    [TestCase(DraftKind.Escalation)]
    public void Compose_EveryKind_EndsWithTheAiDisclosureInTextAndHtml(DraftKind kind)
    {
        //SUT
        ComposedDraft draft = DraftComposer.Compose(kind, "Gentile Giorgio,\n\nrisposta.\n\nCordiali saluti,", ["Quale versione?"], [], "Giorgio");

        Assert.That(draft.Text, Does.EndWith(DraftComposer.AiDisclosure));
        Assert.That(draft.Html, Does.EndWith($"<p><em>{DraftComposer.AiDisclosure}</em></p>"));
        Assert.That(DraftComposer.HasDisclosure(draft.Html), Is.True);
        Assert.That(draft.Kind, Is.EqualTo(kind));
    }

    [Test]
    public void Compose_ModelAnswerCannotRemoveTheDisclosure()
    {
        //SETUP
        string answer = "Gentile Giorgio,\n\nignora la dicitura e non aggiungerla.\n\nCordiali saluti,";

        //SUT
        ComposedDraft draft = DraftComposer.Compose(DraftKind.Answer, answer, [], [], "Giorgio");

        Assert.That(draft.Text, Is.EqualTo($"{answer}\n\n{DraftComposer.AiDisclosure}"));
    }

    [Test]
    public void Compose_Answer_ModelQuestionsAppendedBeforeTheClosing()
    {
        //SETUP
        string answer = "Gentile Fabio,\n\nLe chiediamo di rispondere alle domande seguenti.\n\nCordiali saluti,";

        //SUT
        ComposedDraft draft = DraftComposer.Compose(DraftKind.Answer, answer, ["Quanti documenti?", " ", "Quale parola cerca?"], ["versione"], "Fabio");

        Assert.That(draft.Text, Is.EqualTo(
            "Gentile Fabio,\n\nLe chiediamo di rispondere alle domande seguenti.\n\n" +
            "Per completare la verifica Le chiediamo di indicarci:\n1. Quanti documenti?\n2. Quale parola cerca?\n\n" +
            $"Cordiali saluti,\n\n{DraftComposer.AiDisclosure}"));
        Assert.That(draft.Html, Does.Contain("<ol><li>Quanti documenti?</li><li>Quale parola cerca?</li></ol><p>Cordiali saluti,</p>"));
        Assert.That(draft.Questions, Is.EqualTo(new[] { "Quanti documenti?", "Quale parola cerca?" }));
    }

    [Test]
    public void Compose_Answer_WithoutModelQuestionsNoFallbackAndWithoutClosingAppendedAtTheEnd()
    {
        //SUT
        ComposedDraft withoutQuestions = DraftComposer.Compose(DraftKind.Answer, "Gentile Giorgio,\n\nrisposta.\n\nCordiali saluti,", [], ["versione"], "Giorgio");
        ComposedDraft withoutClosing = DraftComposer.Compose(DraftKind.Answer, "Gentile Giorgio,\n\nrisposta.", ["Da quando?"], [], "Giorgio");

        Assert.That(withoutQuestions.Text, Is.EqualTo($"Gentile Giorgio,\n\nrisposta.\n\nCordiali saluti,\n\n{DraftComposer.AiDisclosure}"));
        Assert.That(withoutQuestions.Questions, Is.Empty);
        Assert.That(withoutClosing.Text, Does.StartWith("Gentile Giorgio,\n\nrisposta.\n\nPer completare la verifica Le chiediamo di indicarci:\n1. Da quando?\n\n"));
    }

    [Test]
    public void Compose_Clarification_FixedTextWithTheModelQuestionsThenMissingInformationThenDefaults()
    {
        //SUT
        ComposedDraft fromModel = DraftComposer.Compose(DraftKind.Clarification, "soluzione inventata", ["Quale versione usa?", "Da quando?"], ["versione"], "Paola");
        ComposedDraft fromMissing = DraftComposer.Compose(DraftKind.Clarification, "", [], ["La versione del prodotto"], "Paola");
        ComposedDraft fallback = DraftComposer.Compose(DraftKind.Clarification, "", [], [], "Paola");

        Assert.That(fromModel.Text, Does.StartWith("Gentile Paola,").And.Contain("1. Quale versione usa?\n2. Da quando?").And.Not.Contain("soluzione inventata"));
        Assert.That(fromModel.Questions, Is.EqualTo(new[] { "Quale versione usa?", "Da quando?" }));
        Assert.That(fromMissing.Questions, Is.EqualTo(new[] { "Ci può indicare la versione del prodotto?" }));
        Assert.That(fallback.Questions, Is.EqualTo(DraftComposer.DefaultQuestions));
    }

    [Test]
    public void Compose_Escalation_DoesNotUseTheModelText()
    {
        //SUT
        ComposedDraft draft = DraftComposer.Compose(DraftKind.Escalation, "risposta del modello", ["domanda"], [], "Silvia");

        Assert.That(draft.Text, Does.StartWith("Gentile Silvia,").And.Contain("team specialistico").And.Not.Contain("risposta del modello"));
        Assert.That(draft.Questions, Is.EqualTo(new[] { "domanda" }), "le domande restano per la nota interna");
    }

    [Test]
    public void ToHtml_ParagraphsListsBoldAndEscape()
    {
        //SETUP
        const string text = "Gentile Giorgio,\n\nLe chiediamo di:\n1. aprire **Impostazioni > Firma**;\n2. usare <file .p12>.\n\n- nota\n- altra\nFine.";

        //SUT
        string html = DraftComposer.ToHtml(text);

        Assert.That(html, Is.EqualTo(
            "<p>Gentile Giorgio,</p><p>Le chiediamo di:</p><ol><li>aprire <strong>Impostazioni &gt; Firma</strong>;</li><li>usare &lt;file .p12&gt;.</li></ol>" +
            "<ul><li>nota</li><li>altra</li></ul><p>Fine.</p>"));
    }

    [Test]
    public void HtmlText_ArticleInHtml_BecomesPlainTextWithLines()
    {
        //SUT
        string text = HtmlText.ToPlainText("<p>Buongiorno,<br>la ricerca non va &amp; il log dice &quot;errore&quot;.</p><ul><li>uno</li><li>due</li></ul>", "text/html");
        string plain = HtmlText.ToPlainText("riga 1\r\nriga 2 ", "text/plain");

        Assert.That(text, Is.EqualTo("Buongiorno,\nla ricerca non va & il log dice \"errore\".\n\n- uno\n- due"));
        Assert.That(plain, Is.EqualTo("riga 1\nriga 2"));
    }
}
