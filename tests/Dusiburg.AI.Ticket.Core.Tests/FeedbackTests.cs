using System.Net;
using Dusiburg.AI.Ticket.Core.Composition;
using Dusiburg.AI.Ticket.Core.Feedback;
using Dusiburg.AI.Ticket.Core.Publishing;
using Dusiburg.AI.Ticket.Core.Tests.Support;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Core.Tests;

/// <summary>Esito della bozza (fase-7.md §1–2): distanza di modifica, regole deterministiche, marcatura P8 su uno Zammad finto.</summary>
public class FeedbackTests
{
    private const string Answer = "Gentile Giorgio,\n\ngrazie per la segnalazione. Le chiediamo di:\n1. aprire Impostazioni > Trasmissione;\n2. riavviare il servizio.\n\nCordiali saluti,";

    private const double MaxDistance = 0.7;

    [Test]
    public void Words_DraftSentAsPublished_IsZeroAndTheDisclosureIsFound()
    {
        //SETUP
        ComposedDraft draft = DraftComposer.Compose(DraftKind.Answer, Answer, [], [], "Giorgio");

        //SUT
        double distance = EditDistance.Words(EditDistance.ComparableDraft(draft.Text), EditDistance.ComparableArticle(draft.Html, "text/html"));

        Assert.That(distance, Is.Zero, "lo stesso testo passa da testo semplice (AgentDraft.Body) e da HTML (articolo di Zammad)");
        Assert.That(EditDistance.HasDisclosure(draft.Html, "text/html"), Is.True);
    }

    [Test]
    public void Words_SmallEditRewriteAndFormatting_DistanceGrowsWithTheChangedWords()
    {
        //SETUP
        ComposedDraft draft = DraftComposer.Compose(DraftKind.Answer, Answer, [], [], "Giorgio");
        string comparable = EditDistance.ComparableDraft(draft.Text);
        string edited = draft.Html.Replace("<p>Cordiali saluti,</p>", "<p>Resto a disposizione.</p><p>Cordiali saluti,</p>", StringComparison.Ordinal);
        string reformatted = draft.Html.Replace("<p>", "<p>  ", StringComparison.Ordinal).Replace("</li>", " </li>", StringComparison.Ordinal);
        const string rewritten = "<p>Buongiorno,</p><p>un tecnico la contatterà in giornata per una verifica in collegamento remoto.</p><p>Un saluto,</p>";

        //SUT
        double small = EditDistance.Words(comparable, EditDistance.ComparableArticle(edited, "text/html"));
        double formatting = EditDistance.Words(comparable, EditDistance.ComparableArticle(reformatted, "text/html"));
        double rewrite = EditDistance.Words(comparable, EditDistance.ComparableArticle(rewritten, "text/html"));

        Assert.That(small, Is.GreaterThan(0).And.LessThan(0.2));
        Assert.That(formatting, Is.Zero, "spazi e a capo non sono modifiche");
        Assert.That(rewrite, Is.GreaterThan(MaxDistance));
    }

    [Test]
    public void Words_DisclosureRemoved_IsNotAnEditButIsDetected()
    {
        //SETUP
        ComposedDraft draft = DraftComposer.Compose(DraftKind.Answer, Answer, [], [], "Giorgio");
        string withoutDisclosure = draft.Html[..draft.Html.IndexOf("<p><em>", StringComparison.Ordinal)];

        //SUT
        double distance = EditDistance.Words(EditDistance.ComparableDraft(draft.Text), EditDistance.ComparableArticle(withoutDisclosure, "text/html"));

        Assert.That(distance, Is.Zero);
        Assert.That(EditDistance.HasDisclosure(withoutDisclosure, "text/html"), Is.False);
    }

    [Test]
    public void Levenshtein_InsertionsDeletionsSubstitutions()
    {
        //SUT
        Assert.That(EditDistance.Levenshtein(["a", "b", "c"], ["a", "b", "c"]), Is.Zero);
        Assert.That(EditDistance.Levenshtein(["a", "b", "c"], ["a", "x", "c", "d"]), Is.EqualTo(2));
        Assert.That(EditDistance.Levenshtein([], ["a", "b"]), Is.EqualTo(2));
        Assert.That(EditDistance.Words("", ""), Is.Zero);
    }

    [Test]
    public void Decide_ReplyAfterTheNote_OutcomeByDistance()
    {
        //SETUP
        TicketDraft[] drafts = [Draft(1, note: 100, "a b c d e f g h i j")];

        //SUT
        OutcomeDecision unchanged = DraftOutcomeRules.Decide(drafts, [Reply(101, "a b c d e f g h i j")], false, MaxDistance).Single();
        OutcomeDecision edited = DraftOutcomeRules.Decide(drafts, [Reply(101, "a b c d e f g h i j k l")], false, MaxDistance).Single();
        OutcomeDecision rewritten = DraftOutcomeRules.Decide(drafts, [Reply(101, "x y z w")], false, MaxDistance).Single();

        Assert.That((unchanged.Outcome, unchanged.EditDistance, unchanged.ReplyArticleId), Is.EqualTo((DraftOutcome.SentUnchanged, 0.0, 101L)));
        Assert.That((edited.Outcome, edited.EditDistance), Is.EqualTo((DraftOutcome.SentEdited, 0.167)));
        Assert.That(rewritten.Outcome, Is.EqualTo(DraftOutcome.Discarded));
        Assert.That(rewritten.Reason, Does.StartWith("risposta scritta dall'operatore"));
        Assert.That(rewritten.DisclosureKept, Is.True, "registrata anche se scartata");
    }

    [Test]
    public void Decide_NoReply_PendingUntilANewerDraftOrTheClosure()
    {
        //SETUP
        TicketDraft[] one = [Draft(1, note: 100, "testo")];
        TicketDraft[] two = [Draft(1, note: 100, "testo"), Draft(2, note: 120, "altro")];

        //SUT
        IReadOnlyList<OutcomeDecision> open = DraftOutcomeRules.Decide(one, [], ticketClosed: false, MaxDistance);
        IReadOnlyList<OutcomeDecision> closed = DraftOutcomeRules.Decide(one, [], ticketClosed: true, MaxDistance);
        IReadOnlyList<OutcomeDecision> superseded = DraftOutcomeRules.Decide(two, [], ticketClosed: false, MaxDistance);

        Assert.That(open, Is.Empty);
        Assert.That((closed.Single().Outcome, closed.Single().Reason), Is.EqualTo((DraftOutcome.Discarded, "ticket chiuso senza risposta dell'operatore")));
        Assert.That(superseded.Select(d => (d.DraftId, d.Outcome)), Is.EqualTo(new[] { (1, DraftOutcome.Discarded) }), "la bozza più recente resta in attesa");
        Assert.That(superseded.Single().Reason, Does.Contain("elaborazione n. 20"));
    }

    [Test]
    public void Decide_RepliesAreMatchedToTheirOwnDraftAndOldRepliesIgnored()
    {
        //SETUP
        TicketDraft[] drafts = [Draft(1, note: 100, "prima bozza"), Draft(2, note: 120, "seconda bozza")];
        AgentReply[] replies = [Reply(90, "risposta precedente alle bozze"), Reply(105, "prima bozza"), Reply(110, "un'altra risposta"), Reply(121, "seconda bozza modificata")];

        //SUT
        IReadOnlyList<OutcomeDecision> decisions = DraftOutcomeRules.Decide(drafts, replies, ticketClosed: false, MaxDistance);

        Assert.That(decisions.Select(d => (d.DraftId, d.Outcome, d.ReplyArticleId)), Is.EqualTo(new[]
        {
            (1, DraftOutcome.SentUnchanged, (long?)105), (2, DraftOutcome.SentEdited, (long?)121)
        }));
    }

    [Test]
    public void Decide_DraftWithAnOutcome_IsNotDecidedAgain()
    {
        //SETUP
        TicketDraft[] drafts = [Draft(1, note: 100, "testo") with { Outcome = DraftOutcome.SentEdited }];

        //SUT
        Assert.That(DraftOutcomeRules.Decide(drafts, [Reply(101, "testo")], ticketClosed: true, MaxDistance), Is.Empty);
    }

    [TestCase(DraftOutcome.SentUnchanged, true, new[] { "ai-generata" })]
    [TestCase(DraftOutcome.SentEdited, true, new[] { "ai-generata-in-parte" })]
    [TestCase(DraftOutcome.SentEdited, false, new[] { "ai-generata-in-parte", "ai-dicitura-rimossa" })]
    [TestCase(DraftOutcome.Discarded, false, new string[0])]
    public async Task MarkOutcome_TagsByOutcomeAndANoteWhenTheDisclosureWasRemoved(DraftOutcome outcome, bool disclosureKept, string[] expectedTags)
    {
        //SETUP
        FakeZammad zammad = new FakeZammad()
            .On("POST", "/api/v1/ticket_articles", """{"id":1001,"ticket_id":170,"internal":true,"body":"x","created_at":"2026-10-19T10:00:00Z"}""")
            .On("POST", "/api/v1/tags/add", "true");

        //SUT
        IReadOnlyList<string> tags = await new ZammadPublisher(zammad.Client()).MarkOutcomeAsync(170, outcome, 1000, disclosureKept, runId: 7, CancellationToken.None);

        Assert.That(tags, Is.EqualTo(expectedTags));
        Assert.That(zammad.Writes.Count(w => w.Path == "/api/v1/tags/add"), Is.EqualTo(expectedTags.Length));

        var notes = zammad.Writes.Where(w => w.Path == "/api/v1/ticket_articles").ToList();
        Assert.That(notes, Has.Count.EqualTo(expectedTags.Contains(ZammadPublisher.DisclosureRemovedTag) ? 1 : 0));
        Assert.That(notes.Select(n => n.Body), Has.All.Contain("\"internal\":true").And.All.Contain("articolo 1000"));
    }

    private static TicketDraft Draft(int id, int note, string text) => new(id, id * 10, note, DraftOutcome.Unknown, text);

    private static AgentReply Reply(long id, string text) => new(id, text, text, HasDisclosure: true);
}
