using System.Diagnostics;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Core.Publishing;
using Dusiburg.AI.Ticket.Core.Verification;
using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Configurations;
using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Telemetry;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dusiburg.AI.Ticket.Core.Pipeline;

/// <summary>
/// Le elaborazioni registrate (<c>agent.AgentRun</c>, P2): creazione idempotente su (ticket, articolo), esecuzione della pipeline,
/// bozza e fonti in <c>agent.AgentDraft</c>. Un errore di Ollama, di Zammad o del database chiude l'elaborazione in <c>Failed</c>
/// con codice e messaggio, senza bozza registrata.
/// </summary>
public sealed class AgentRunService(TicketDbContext db, TicketPipeline pipeline, TimeProvider timeProvider, ILogger<AgentRunService> logger)
{
    private static readonly ActivitySource Telemetry = new(TicketTelemetry.Sources.Pipeline);

    /// <summary>Errori di Zammad e di rete verso i servizi (Ollama, Zammad).</summary>
    public const string ZammadErrorCode = "ZAMMAD_ERROR";
    public const string UpstreamErrorCode = "UPSTREAM_UNAVAILABLE";
    public const string InternalErrorCode = "INTERNAL";

    /// <summary>L'elaborazione nuova, oppure <c>null</c> se (ticket, articolo) è già stato ricevuto: doppia consegna o polling che rilegge.</summary>
    public async Task<int?> TryCreateAsync(long ticketId, long articleId, RunTrigger trigger, CancellationToken cancellationToken)
    {
        var run = new AgentRun
        {
            ZammadTicketId = checked((int)ticketId),
            ZammadArticleId = checked((int)articleId),
            Trigger = trigger,
            State = AgentRunState.Queued,
            ReceivedAt = timeProvider.GetUtcNow()
        };

        db.AgentRuns.Add(run);

        try
        {
            await db.SaveChangesAsync(cancellationToken);

            return run.Id;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();

            return null;
        }
    }

    /// <summary>Elaborazioni rimaste in coda o interrotte (Agent riavviato): si riprendono all'avvio.</summary>
    public async Task<IReadOnlyList<int>> PendingAsync(CancellationToken cancellationToken) =>
        await db.AgentRuns.Where(r => r.State == AgentRunState.Queued || r.State == AgentRunState.Running)
            .OrderBy(r => r.Id).Select(r => r.Id).ToListAsync(cancellationToken);

    public async Task<AgentRun> ProcessAsync(int runId, Func<string, string?>? traceUrl, CancellationToken cancellationToken)
    {
        AgentRun run = await db.AgentRuns.SingleAsync(r => r.Id == runId, cancellationToken);

        using Activity? activity = Telemetry.StartActivity("elaborazione");
        activity?.SetTag("ticket.zammad_id", run.ZammadTicketId);
        activity?.SetTag("ticket.article_id", run.ZammadArticleId);

        run.State = AgentRunState.Running;
        run.StartedAt = timeProvider.GetUtcNow();
        run.TraceId = activity?.TraceId.ToHexString();
        run.Mode = pipeline.Options.Mode;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            DraftProposal proposal = await pipeline.ProposeAsync(run.ZammadTicketId, run.ZammadArticleId, cancellationToken);
            run.TicketNumber = Truncate(proposal.Ticket.Number, ColumnSizes.TicketNumber);
            run.TicketTitle = Truncate(proposal.Ticket.Title, ColumnSizes.Title);

            (proposal, PublicationResult publication) = await pipeline.PublishAsync(
                proposal, run.Id, run.TraceId is { } trace ? traceUrl?.Invoke(trace) : null, cancellationToken);

            db.AgentDrafts.Add(ToEntity(run.Id, proposal, publication.NoteArticleId));
            run.ToolCalls.AddRange(ToEntities(proposal.Calls));
            Complete(run, AgentRunState.Completed, null, null);
            activity?.SetTag("ticket.tags", string.Join(",", publication.Tags));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Elaborazione {RunId} del ticket {TicketId} non riuscita", run.Id, run.ZammadTicketId);
            db.ChangeTracker.Entries<AgentDraft>().ToList().ForEach(e => e.State = EntityState.Detached);
            db.ChangeTracker.Entries<AgentToolCall>().ToList().ForEach(e => e.State = EntityState.Detached);
            run.ToolCalls.Clear();
            Complete(run, AgentRunState.Failed, CodeOf(exception), exception.Message);
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        }

        await db.SaveChangesAsync(CancellationToken.None);

        return run;
    }

    /// <summary>Articolo che non va elaborato (nota interna, mittente non cliente): registrato come <c>Skipped</c>.</summary>
    public async Task SkipAsync(int runId, string reason, CancellationToken cancellationToken)
    {
        AgentRun run = await db.AgentRuns.SingleAsync(r => r.Id == runId, cancellationToken);
        Complete(run, AgentRunState.Skipped, null, reason);
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static AgentDraft ToEntity(int runId, DraftProposal proposal, long noteArticleId)
    {
        ModelClassification classification = proposal.Classification.Value;
        ModelDraft model = proposal.ModelDraft.Value;
        ConfidenceAssessment confidence = proposal.Confidence;
        PipelineTimings timings = proposal.Timings;
        Dictionary<string, VerifiedCitation> cited = proposal.Verification.Citations.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

        var draft = new AgentDraft
        {
            AgentRunId = runId,
            Kind = confidence.Kind,
            Category = Truncate(classification.Category, ColumnSizes.Category),
            Priority = classification.Priority switch { "alta" => TicketPriority.High, "bassa" => TicketPriority.Low, _ => TicketPriority.Normal },
            Language = Truncate(classification.Language, ColumnSizes.Language),
            MissingInformation = [.. classification.MissingInformation],
            CanAnswer = model.CanAnswer,
            SelfAssessment = model.SelfAssessment,
            RetrievalScore = confidence.Retrieval,
            CitationScore = confidence.Citation,
            CoverageScore = confidence.Coverage,
            Confidence = confidence.Confidence,
            Body = proposal.Draft.Text,
            Questions = [.. proposal.Draft.Questions],
            ChatModel = Truncate(proposal.ModelDraft.ModelId, ColumnSizes.ModelName),
            PromptHash = proposal.ModelDraft.PromptHash,
            ContextMs = Milliseconds(timings.Context),
            ClassificationMs = Milliseconds(timings.Classification),
            RetrievalMs = Milliseconds(timings.Retrieval),
            GenerationMs = Milliseconds(timings.Generation),
            VerificationMs = Milliseconds(timings.Verification),
            PublicationMs = Milliseconds(timings.Publication),
            NoteArticleId = checked((int)noteArticleId),
            CreatedAt = DateTimeOffset.UtcNow
        };

        draft.Sources.AddRange(proposal.Retrieval.PromptSources.Select((RetrievedSource source, int index) => new AgentDraftSource
        {
            Rank = (byte)(index + 1),
            Source = source.Source,
            SourceKey = source.Key,
            ChunkId = source.Best.Chunk.Id,
            Reason = cited.TryGetValue(source.Key, out VerifiedCitation? citation) ? Truncate(citation.Reason, ColumnSizes.Reason) : null,
            IsCitedByModel = citation is not null,
            IsVerified = citation?.IsVerified ?? false
        }));

        return draft;
    }

    /// <summary>Le chiamate ai tool della modalità agente, nell'ordine; argomenti e messaggi tagliati alla colonna.</summary>
    internal static IEnumerable<AgentToolCall> ToEntities(IReadOnlyList<ToolCallRecord> calls) =>
        calls.Take(byte.MaxValue).Select((call, index) => new AgentToolCall
        {
            Sequence = (byte)(index + 1),
            ToolName = Truncate(call.ToolName, ColumnSizes.ToolName),
            Arguments = Truncate(call.Arguments, ColumnSizes.ToolArguments),
            Outcome = Truncate(call.Outcome, ColumnSizes.ErrorCode),
            DurationMs = Milliseconds(call.Duration),
            Message = call.Message is { Length: > 0 } message ? Truncate(message, ColumnSizes.Reason) : null
        });

    private void Complete(AgentRun run, AgentRunState state, string? errorCode, string? error)
    {
        run.State = state;
        run.CompletedAt = timeProvider.GetUtcNow();
        run.DurationMs = run.StartedAt is { } started ? (int)(run.CompletedAt.Value - started).TotalMilliseconds : null;
        run.ErrorCode = errorCode;
        run.Error = error is null ? null : Truncate(error, ColumnSizes.Error);
    }

    internal static string CodeOf(Exception exception) => exception switch
    {
        InvalidModelOutputException => InvalidModelOutputException.Code,
        ZammadApiException => ZammadErrorCode,
        HttpRequestException or TimeoutException or TaskCanceledException => UpstreamErrorCode,
        _ => InternalErrorCode
    };

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private static int Milliseconds(TimeSpan duration) => (int)Math.Round(duration.TotalMilliseconds);
}
