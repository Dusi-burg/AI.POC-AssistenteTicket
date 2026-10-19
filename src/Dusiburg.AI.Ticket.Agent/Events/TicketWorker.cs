using Dusiburg.AI.Ticket.Core;
using Dusiburg.AI.Ticket.Core.Pipeline;
using Dusiburg.AI.Ticket.Data.Entities;

namespace Dusiburg.AI.Ticket.Agent.Events;

/// <summary>
/// L'unico consumatore della coda (P2): un'elaborazione alla volta. All'avvio riprende quelle rimaste in coda o interrotte.
/// Un errore resta nell'elaborazione (<c>Failed</c>) e non ferma il consumatore.
/// </summary>
public sealed class TicketWorker(TicketWorkQueue queue, IServiceScopeFactory scopes, AgentOptions options, ILogger<TicketWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeuePendingAsync(stoppingToken);

        await foreach (int runId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using IServiceScope scope = scopes.CreateScope();
                AgentRun run = await scope.ServiceProvider.GetRequiredService<AgentRunService>().ProcessAsync(runId, TraceUrl, stoppingToken);

                logger.LogInformation("Elaborazione {RunId} del ticket {TicketId}: {State} in {DurationMs} ms", run.Id, run.ZammadTicketId, run.State, run.DurationMs);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Elaborazione {RunId}: errore fuori dalla pipeline", runId);
            }
        }
    }

    /// <summary>Link alla traccia nel dashboard di Aspire (<c>TICKET_DASHBOARD_URL</c>), per la nota interna.</summary>
    private string? TraceUrl(string traceId) =>
        options.DashboardUrl is { } dashboard ? new Uri(dashboard, $"traces/detail/{traceId}").ToString() : null;

    private async Task RequeuePendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            IReadOnlyList<int> pending = await scope.ServiceProvider.GetRequiredService<AgentRunService>().PendingAsync(cancellationToken);

            foreach (int runId in pending)
            {
                queue.Enqueue(runId);
            }

            if (pending.Count > 0)
            {
                logger.LogInformation("Riprese {Count} elaborazioni rimaste in coda", pending.Count);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Database non ancora creato (DbInit): l'Agent parte lo stesso, lo segnala /api/health.
            logger.LogWarning(exception, "Elaborazioni in sospeso non lette");
        }
    }
}
