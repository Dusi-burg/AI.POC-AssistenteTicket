using Dusiburg.AI.Ticket.Shared.Health;

namespace Dusiburg.AI.Ticket.Agent.Tests.Support;

/// <summary>Probe con esiti fissi: sostituisce SQL e Ollama nei test degli endpoint.</summary>
internal sealed class StubHealthProbe(params ProbeResult[] results) : IHealthProbe
{
    public Task<IReadOnlyList<ProbeResult>> RunAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProbeResult>>(results);
}
