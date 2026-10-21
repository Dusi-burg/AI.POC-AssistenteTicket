using Dusiburg.AI.Ticket.Agent.Events;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.ServiceDefaults.Problems;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Runs;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Agent.Runs;

/// <summary>
/// <c>/api/runs</c>: avvio manuale di un'elaborazione (demo, prove senza webhook) e viste sulle elaborazioni per la console
/// (Fase 7): elenco filtrabile, dettaglio con segnali, fonti, tempi per passo ed esito.
/// </summary>
public static class RunEndpoints
{
    public const int ListSize = 50;

    public static RouteGroupBuilder MapRunEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/runs", StartAsync);
        group.MapGet("/runs", ListAsync);
        group.MapGet("/runs/{id:int}", GetAsync);

        return group;
    }

    /// <summary>Senza articolo si elabora l'ultimo articolo pubblico del cliente; un articolo già elaborato è un conflitto.</summary>
    internal static async Task<Results<Accepted<StartRunResponse>, ProblemHttpResult>> StartAsync(
        StartRunRequest request, ZammadClient zammad, TicketEventIntake intake, CancellationToken cancellationToken)
    {
        IReadOnlyList<ZammadArticleDetail> articles;

        try
        {
            articles = await zammad.GetArticlesAsync(request.TicketId, cancellationToken);
        }
        catch (ZammadApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return ApiProblems.NotFound($"Ticket {request.TicketId} inesistente in Zammad.");
        }

        ZammadArticleDetail? article = request.ArticleId is { } articleId
            ? articles.SingleOrDefault(a => a.Id == articleId)
            : articles.LastOrDefault(a => a.IsFromCustomer);

        if (article is null || !article.IsFromCustomer)
        {
            return article is null
                ? ApiProblems.NotFound($"Ticket {request.TicketId}: nessun articolo del cliente{(request.ArticleId is { } id ? $" con id {id}" : "")}.")
                : ApiProblems.Validation($"Articolo {article.Id}: non è un articolo pubblico del cliente.");
        }

        IntakeResult result = await intake.AcceptAsync(
            new TicketEvent(request.TicketId, article.Id, article.Sender, article.Internal, RunTrigger.Manual), cancellationToken);

        return result.Outcome == IntakeOutcome.Queued
            ? TypedResults.Accepted($"/api/runs/{result.RunId}", new StartRunResponse(result.Outcome.ToString(), result.RunId))
            : ApiProblems.Conflict($"Articolo {article.Id} del ticket {request.TicketId} già elaborato.");
    }

    internal static async Task<Ok<List<RunSummary>>> ListAsync([AsParameters] RunFilter filter, TicketDbContext db, CancellationToken cancellationToken)
    {
        IQueryable<AgentRun> runs = db.AgentRuns.AsNoTracking();

        if (filter.State is { } state)
        {
            runs = runs.Where(r => r.State == state);
        }

        if (filter.Kind is { } kind)
        {
            runs = runs.Where(r => r.Draft != null && r.Draft.Kind == kind);
        }

        if (filter.Outcome is { } outcome)
        {
            runs = runs.Where(r => r.Draft != null && r.Draft.Outcome == outcome);
        }

        return TypedResults.Ok(await runs
            .OrderByDescending(r => r.Id)
            .Take(ListSize)
            .Select(r => Summary(r, r.Draft))
            .ToListAsync(cancellationToken));
    }

    internal static async Task<Results<Ok<RunDetail>, ProblemHttpResult>> GetAsync(int id, TicketDbContext db, CancellationToken cancellationToken)
    {
        AgentRun? run = await db.AgentRuns.AsNoTracking()
            .Include(r => r.Draft).ThenInclude(d => d!.Sources)
            .Include(r => r.ToolCalls)
            .AsSplitQuery()
            .SingleOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (run is null)
        {
            return ApiProblems.NotFound($"Elaborazione {id} inesistente.");
        }

        AgentDraft? d = run.Draft;
        RunDraftView? draft = d is null ? null : new RunDraftView(
            d.Kind, d.Category, d.Priority, d.MissingInformation, d.CanAnswer, d.SelfAssessment, d.RetrievalScore, d.CitationScore, d.CoverageScore,
            d.Confidence, d.Body, d.Questions, d.ChatModel,
            new RunTimings(d.ContextMs, d.ClassificationMs, d.RetrievalMs, d.GenerationMs, d.VerificationMs, d.PublicationMs),
            new RunOutcomeView(d.Outcome, d.OutcomeAt, d.OutcomeReason, d.ReplyArticleId, d.FinalText, d.EditDistance, d.DisclosureKept),
            [.. d.Sources.OrderBy(s => s.Rank).Select(s => new RunSourceView(s.Rank, s.Source, s.SourceKey, s.IsCitedByModel, s.IsVerified, s.Reason))]);

        return TypedResults.Ok(new RunDetail(Summary(run, d), draft,
            [.. run.ToolCalls.OrderBy(c => c.Sequence).Select(c => new RunToolCallView(c.Sequence, c.ToolName, c.Arguments, c.Outcome, c.DurationMs, c.Message))]));
    }

    private static RunSummary Summary(AgentRun r, AgentDraft? d) => new(
        r.Id, r.ZammadTicketId, r.ZammadArticleId, r.TicketNumber, r.TicketTitle, r.Trigger, r.State, r.ReceivedAt, r.DurationMs, r.ErrorCode, r.Error,
        r.TraceId, d == null ? null : d.Kind, d == null ? null : d.Confidence, d == null ? null : d.Outcome, r.Mode);
}
