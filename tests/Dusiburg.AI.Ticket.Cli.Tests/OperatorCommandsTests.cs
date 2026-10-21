using Dusiburg.AI.Ticket.Cli.Commands;
using Dusiburg.AI.Ticket.Core.Composition;
using Dusiburg.AI.Ticket.Core.Feedback;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Cli.Tests;

/// <summary><c>reply</c>: le modifiche dell'operatore simulato portano agli esiti attesi della Fase 7.</summary>
public class OperatorCommandsTests
{
    private static readonly ComposedDraft Draft = DraftComposer.Compose(
        DraftKind.Answer, "Gentile Giorgio,\n\nverifichi il certificato del canale e riavvii il servizio.\n\nCordiali saluti,", [], [], "Giorgio");

    [Test]
    public void Edit_AsIs_SameHtml()
    {
        //SUT
        Assert.That(OperatorCommands.Edit(Draft.Html, edit: false, rewrite: false, withoutDisclosure: false), Is.EqualTo(Draft.Html));
    }

    [Test]
    public void Edit_EachOption_GivesTheExpectedDistanceAndDisclosure()
    {
        //SETUP
        string comparable = EditDistance.ComparableDraft(Draft.Text);

        //SUT
        string edited = OperatorCommands.Edit(Draft.Html, edit: true, rewrite: false, withoutDisclosure: false);
        string rewritten = OperatorCommands.Edit(Draft.Html, edit: false, rewrite: true, withoutDisclosure: false);
        string withoutDisclosure = OperatorCommands.Edit(Draft.Html, edit: true, rewrite: false, withoutDisclosure: true);

        Assert.That(edited, Does.Contain(OperatorCommands.EditSentence).And.EndWith($"<p><em>{DraftComposer.AiDisclosure}</em></p>"), "la frase va prima della dicitura");
        Assert.That(EditDistance.Words(comparable, EditDistance.ComparableArticle(edited, "text/html")), Is.GreaterThan(0).And.LessThanOrEqualTo(0.7));
        Assert.That(EditDistance.Words(comparable, EditDistance.ComparableArticle(rewritten, "text/html")), Is.GreaterThan(0.7));
        Assert.That(EditDistance.HasDisclosure(rewritten, "text/html"), Is.True);
        Assert.That(EditDistance.HasDisclosure(withoutDisclosure, "text/html"), Is.False);
        Assert.That(withoutDisclosure, Does.Contain(OperatorCommands.EditSentence));
    }
}
