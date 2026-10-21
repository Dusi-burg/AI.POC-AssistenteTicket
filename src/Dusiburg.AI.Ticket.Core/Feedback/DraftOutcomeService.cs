using System.Diagnostics;
using Dusiburg.AI.Ticket.Core.Context;
using Dusiburg.AI.Ticket.Core.Publishing;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Configurations;
using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Telemetry;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dusiburg.AI.Ticket.Core.Feedback;

/// <summary>Esito registrato per una bozza, con i tag P8 scritti in Zammad.</summary>
public sealed record ResolvedOutcome(int RunId, int DraftId, DraftOutcome Outcome, double? EditDistance, bool? DisclosureKept, IReadOnlyList<string> Tags, string Reason);

/// <summary>
/// Raccolta dell'esito (fase-7.md §1–2): un solo controllo per ticket, chiamato da qualunque evento (risposta dell'operatore,
/// chiusura, polling, comando manuale). Legge articoli e stato da Zammad, decide con <see cref="DraftOutcomeRules"/>, scrive tag
/// e nota P8 e solo dopo salva l'esito: se Zammad non risponde l'evento si ripete. Un controllo alla volta, così due consegne
/// contemporanee (risposta e chiusura insieme) non scrivono due volte la stessa nota.
/// </summary>
public sealed class DraftOutcomeService(
    TicketDbContext db, ZammadClient zammad, ZammadPublisher publisher, AgentOptions options, TimeProvider timeProvider, ILogger<DraftOutcomeService> logger)
{
    private static readonly ActivitySource Telemetry = new(TicketTelemetry.Sources.Pipeline);

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>Ticket con bozze in attesa: il polling controlla solo questi.</summary>
    public async Task<bool> HasPendingDraftsAsync(long ticketId, CancellationToken cancellationToken) =>
        await PendingQuery(checked((int)ticketId)).AnyAsync(cancellationToken);

    public async Task<IReadOnlyList<ResolvedOutcome>> CheckTicketAsync(long ticketId, CancellationToken cancellationToken)
    {
        await OneAtATime.WaitAsync(cancellationToken);

        try
        {
            return await CheckCoreAsync(checked((int)ticketId), cancellationToken);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private async Task<IReadOnlyList<ResolvedOutcome>> CheckCoreAsync(int ticketId, CancellationToken cancellationToken)
    {
        if (!await PendingQuery(ticketId).AnyAsync(cancellationToken))
        {
            return [];
        }

        using Activity? activity = Telemetry.StartActivity("esito");
        activity?.SetTag("ticket.zammad_id", ticketId);

        List<AgentDraft> drafts = await (
            from d in db.AgentDrafts
            join r in db.AgentRuns on d.AgentRunId equals r.Id
            where r.ZammadTicketId == ticketId
            orderby d.NoteArticleId
            select d).ToListAsync(cancellationToken);

        ZammadTicketDetail ticket = await zammad.GetTicketAsync(ticketId, cancellationToken);
        IReadOnlyList<ZammadArticleDetail> articles = await zammad.GetArticlesAsync(ticketId, cancellationToken);

        IReadOnlyList<OutcomeDecision> decisions = DraftOutcomeRules.Decide(
            [.. drafts.Select(d => new TicketDraft(d.Id, d.AgentRunId, d.NoteArticleId, d.Outcome, EditDistance.ComparableDraft(d.Body)))],
            [.. articles.Where(a => a.IsAgentReply).Select(Reply)],
            ticket.IsClosed,
            options.FeedbackMaxEditDistance);

        List<ResolvedOutcome> resolved = [];

        foreach (OutcomeDecision decision in decisions)
        {
            IReadOnlyList<string> tags = await publisher.MarkOutcomeAsync(
                ticketId, decision.Outcome, decision.ReplyArticleId, decision.DisclosureKept, decision.RunId, cancellationToken);

            Apply(drafts.Single(d => d.Id == decision.DraftId), decision);
            await db.SaveChangesAsync(cancellationToken);

            resolved.Add(new ResolvedOutcome(decision.RunId, decision.DraftId, decision.Outcome, decision.EditDistance, decision.DisclosureKept, tags, decision.Reason));
            logger.LogInformation("Bozza dell'elaborazione {RunId} (ticket {TicketId}): {Outcome}, {Reason}", decision.RunId, ticketId, decision.Outcome, decision.Reason);
        }

        activity?.SetTag("ticket.outcomes", string.Join(",", resolved.Select(r => r.Outcome)));

        return resolved;
    }

    private IQueryable<AgentDraft> PendingQuery(int ticketId) =>
        from d in db.AgentDrafts
        join r in db.AgentRuns on d.AgentRunId equals r.Id
        where r.ZammadTicketId == ticketId && d.Outcome == DraftOutcome.Unknown
        select d;

    private static AgentReply Reply(ZammadArticleDetail article) => new(
        article.Id,
        EditDistance.ComparableArticle(article.Body, article.ContentType),
        HtmlText.ToPlainText(article.Body, article.ContentType),
        EditDistance.HasDisclosure(article.Body, article.ContentType));

    private void Apply(AgentDraft draft, OutcomeDecision decision)
    {
        draft.Outcome = decision.Outcome;
        draft.ReplyArticleId = decision.ReplyArticleId is { } id ? checked((int)id) : null;
        draft.FinalText = decision.FinalText;
        draft.EditDistance = decision.EditDistance;
        draft.DisclosureKept = decision.DisclosureKept;
        draft.OutcomeReason = decision.Reason.Length <= ColumnSizes.Reason ? decision.Reason : decision.Reason[..ColumnSizes.Reason];
        draft.OutcomeAt = timeProvider.GetUtcNow();
    }
}
