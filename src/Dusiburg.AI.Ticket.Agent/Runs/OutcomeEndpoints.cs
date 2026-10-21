using Dusiburg.AI.Ticket.Agent.Events;
using Dusiburg.AI.Ticket.Core;
using Dusiburg.AI.Ticket.Core.Feedback;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Runs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Agent.Runs;

/// <summary>
/// <c>/api/outcomes</c> (Fase 7): riepilogo degli esiti per la pagina Esiti della console, con gli avvisi P8 delle diciture tolte,
/// e controllo manuale dell'esito delle bozze di un ticket (la stessa operazione di webhook e polling).
/// </summary>
public static class OutcomeEndpoints
{
    public static RouteGroupBuilder MapOutcomeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/outcomes", SummaryAsync);
        group.MapPost("/outcomes/check", CheckAsync);

        return group;
    }

    internal static async Task<Ok<CheckOutcomeResponse>> CheckAsync(CheckOutcomeRequest request, TicketEventIntake intake, CancellationToken cancellationToken)
    {
        IReadOnlyList<ResolvedOutcome> resolved = await intake.CheckOutcomesAsync(request.TicketId, cancellationToken);

        return TypedResults.Ok(new CheckOutcomeResponse(request.TicketId,
            [.. resolved.Select(r => new CheckedOutcome(r.RunId, r.Outcome, r.EditDistance, r.DisclosureKept, r.Tags, r.Reason))]));
    }

    internal static async Task<Ok<OutcomeSummary>> SummaryAsync(TicketDbContext db, AgentOptions options, CancellationToken cancellationToken)
    {
        var drafts = await db.AgentDrafts.AsNoTracking()
            .Select(d => new { d.Kind, d.Outcome, d.EditDistance })
            .ToListAsync(cancellationToken);

        List<OutcomeCount> counts = [.. drafts
            .GroupBy(d => (d.Kind, d.Outcome))
            .OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Outcome)
            .Select(g => new OutcomeCount(g.Key.Kind, g.Key.Outcome, g.Count()))];

        var sent = drafts.Where(d => d.Outcome is DraftOutcome.SentUnchanged or DraftOutcome.SentEdited).ToList();
        int useful = sent.Count(d => d.Outcome == DraftOutcome.SentUnchanged || d.EditDistance <= DraftOutcomeRules.UsefulMaxEditDistance);

        List<DisclosureRemoval> removals = await (
            from d in db.AgentDrafts.AsNoTracking()
            join r in db.AgentRuns.AsNoTracking() on d.AgentRunId equals r.Id
            where d.DisclosureKept == false && (d.Outcome == DraftOutcome.SentUnchanged || d.Outcome == DraftOutcome.SentEdited)
            orderby d.OutcomeAt descending
            select new DisclosureRemoval(r.Id, r.ZammadTicketId, r.TicketNumber, d.ReplyArticleId, d.OutcomeAt, d.Outcome))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new OutcomeSummary(
            drafts.Count,
            drafts.Count(d => d.Outcome == DraftOutcome.Unknown),
            counts,
            sent.Count == 0 ? null : Math.Round(sent.Average(d => d.EditDistance ?? 0), 3),
            useful,
            drafts.Count(d => d.Outcome != DraftOutcome.Unknown),
            DraftOutcomeRules.UsefulMaxEditDistance,
            options.FeedbackMaxEditDistance,
            removals));
    }
}
