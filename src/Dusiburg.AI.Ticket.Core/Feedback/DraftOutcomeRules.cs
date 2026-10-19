using System.Globalization;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Core.Feedback;

/// <summary>Una bozza del ticket, in ordine di pubblicazione: <see cref="NoteArticleId"/> è la nota interna pubblicata con lei.</summary>
public sealed record TicketDraft(int DraftId, int RunId, int NoteArticleId, DraftOutcome Outcome, string ComparableText);

/// <summary>Una risposta pubblica dell'operatore, già ridotta a testo confrontabile.</summary>
public sealed record AgentReply(long ArticleId, string ComparableText, string PlainText, bool HasDisclosure);

/// <param name="ReplyArticleId">L'articolo inviato a partire dalla bozza; <c>null</c> per una bozza superata o un ticket chiuso.</param>
public sealed record OutcomeDecision(
    int DraftId, int RunId, DraftOutcome Outcome, long? ReplyArticleId, string? FinalText, double? EditDistance, bool? DisclosureKept, string Reason);

/// <summary>
/// Esito delle bozze in attesa (fase-7.md §1), deterministico e senza I/O. Per ogni bozza <c>Unknown</c>, la risposta è il primo
/// articolo pubblico dell'operatore dopo la sua nota interna e prima della nota della bozza successiva:
/// <list type="bullet">
/// <item>distanza 0 → <c>SentUnchanged</c>; fino a <c>maxEditDistance</c> → <c>SentEdited</c>; oltre → <c>Discarded</c>;</item>
/// <item>nessuna risposta ma una bozza più recente → <c>Discarded</c> (superata);</item>
/// <item>nessuna risposta e ticket chiuso → <c>Discarded</c>;</item>
/// <item>altrimenti nessuna decisione: la bozza resta in attesa.</item>
/// </list>
/// </summary>
public static class DraftOutcomeRules
{
    /// <summary>"Piccole modifiche" del criterio di successo (PLAN §1): una bozza inviata fin qui conta come utile.</summary>
    public const double UsefulMaxEditDistance = 0.3;

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public static IReadOnlyList<OutcomeDecision> Decide(
        IReadOnlyList<TicketDraft> drafts, IReadOnlyList<AgentReply> replies, bool ticketClosed, double maxEditDistance)
    {
        List<TicketDraft> ordered = [.. drafts.OrderBy(d => d.NoteArticleId)];
        List<OutcomeDecision> decisions = [];

        for (int i = 0; i < ordered.Count; i++)
        {
            TicketDraft draft = ordered[i];

            if (draft.Outcome != DraftOutcome.Unknown)
            {
                continue;
            }

            TicketDraft? next = i + 1 < ordered.Count ? ordered[i + 1] : null;
            AgentReply? reply = replies
                .Where(r => r.ArticleId > draft.NoteArticleId && (next is null || r.ArticleId < next.NoteArticleId))
                .MinBy(r => r.ArticleId);

            OutcomeDecision? decision = reply is not null
                ? FromReply(draft, reply, maxEditDistance)
                : next is not null
                    ? Without(draft, $"superata dalla bozza dell'elaborazione n. {next.RunId} senza risposta dell'operatore")
                    : ticketClosed
                        ? Without(draft, "ticket chiuso senza risposta dell'operatore")
                        : null;

            if (decision is not null)
            {
                decisions.Add(decision);
            }
        }

        return decisions;
    }

    private static OutcomeDecision FromReply(TicketDraft draft, AgentReply reply, double maxEditDistance)
    {
        double distance = EditDistance.Words(draft.ComparableText, reply.ComparableText);

        (DraftOutcome outcome, string reason) = distance == 0
            ? (DraftOutcome.SentUnchanged, "inviata senza modifiche")
            : distance <= maxEditDistance
                ? (DraftOutcome.SentEdited, $"inviata con modifiche (distanza {Number(distance)} ≤ {Number(maxEditDistance)})")
                : (DraftOutcome.Discarded, $"risposta scritta dall'operatore (distanza {Number(distance)} > {Number(maxEditDistance)})");

        return new OutcomeDecision(draft.DraftId, draft.RunId, outcome, reply.ArticleId, reply.PlainText, distance, reply.HasDisclosure, reason);
    }

    private static OutcomeDecision Without(TicketDraft draft, string reason) =>
        new(draft.DraftId, draft.RunId, DraftOutcome.Discarded, null, null, null, null, reason);

    private static string Number(double value) => value.ToString("0.00", Italian);
}
