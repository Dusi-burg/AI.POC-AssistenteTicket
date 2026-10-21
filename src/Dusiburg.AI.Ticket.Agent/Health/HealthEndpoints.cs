namespace Dusiburg.AI.Ticket.Agent.Health;

public static class HealthEndpoints
{
    public static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder group)
    {
        // Esito dettagliato dei controlli, con la stessa cache del health check letto da Aspire.
        group.MapGet("/health", async (HealthReportCache cache, CancellationToken cancellationToken) =>
            TypedResults.Ok(await cache.GetAsync(cancellationToken)));

        return group;
    }
}
