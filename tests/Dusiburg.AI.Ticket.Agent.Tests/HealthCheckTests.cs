using Dusiburg.AI.Ticket.Agent.Health;
using Dusiburg.AI.Ticket.Shared.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dusiburg.AI.Ticket.Agent.Tests;

/// <summary>Traduzione degli esiti di <c>/api/health</c> nel colore della risorsa nel dashboard di Aspire.</summary>
public class HealthCheckTests
{
    [TestCase(ProbeStatus.Ok, HealthStatus.Healthy)]
    [TestCase(ProbeStatus.Info, HealthStatus.Healthy)]
    [TestCase(ProbeStatus.Warning, HealthStatus.Degraded)]
    [TestCase(ProbeStatus.Error, HealthStatus.Unhealthy)]
    public async Task CheckHealthAsync_WorstStatus_DecidesTheResult(ProbeStatus worst, HealthStatus expected)
    {
        //SETUP
        var service = new HealthService([new FixedProbe(
            new ProbeResult(1, "Connessione SQL", ProbeStatus.Ok, "ok", TimeSpan.Zero),
            new ProbeResult(11, "Zammad raggiungibile", worst, "dettaglio", TimeSpan.Zero))]);
        var check = new TicketHealthCheck(new HealthReportCache(service, TimeProvider.System));

        //SUT
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.That(result.Status, Is.EqualTo(expected));
        Assert.That(result.Data.Keys, Does.Contain("11 Zammad raggiungibile"));
    }

    [Test]
    public async Task GetAsync_WithinCacheDuration_RunsTheProbesOnce()
    {
        //SETUP
        var probe = new FixedProbe(new ProbeResult(5, "Ollama raggiungibile", ProbeStatus.Ok, "ok", TimeSpan.Zero));
        var cache = new HealthReportCache(new HealthService([probe]), TimeProvider.System);

        //SUT
        await cache.GetAsync(CancellationToken.None);
        await cache.GetAsync(CancellationToken.None);

        Assert.That(probe.Runs, Is.EqualTo(1));
    }

    private sealed class FixedProbe(params ProbeResult[] results) : IHealthProbe
    {
        public int Runs { get; private set; }

        public Task<IReadOnlyList<ProbeResult>> RunAsync(CancellationToken cancellationToken)
        {
            Runs++;

            return Task.FromResult<IReadOnlyList<ProbeResult>>(results);
        }
    }
}
