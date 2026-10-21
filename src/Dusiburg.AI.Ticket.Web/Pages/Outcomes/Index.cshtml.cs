using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Runs;
using Dusiburg.AI.Ticket.Web.Api;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Dusiburg.AI.Ticket.Web.Pages.Outcomes;

/// <summary>
/// Esiti (Fase 7): che cosa hanno fatto gli operatori delle bozze, quante erano utili (criterio del PLAN §1) e gli avvisi P8 delle
/// risposte inviate senza la dicitura AI.
/// </summary>
public sealed class IndexModel(AgentApiClient agent, IOptions<ConsoleLinks> links) : PageModel
{
    public OutcomeSummary? Summary { get; private set; }

    public string? Error { get; private set; }

    public ConsoleLinks Links => links.Value;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            Summary = await agent.GetOutcomesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Error = $"Agent non raggiungibile: {exception.Message}";
        }
    }

    /// <summary>Bozze di un tipo con un esito (0 se nessuna).</summary>
    public int Count(DraftKind kind, DraftOutcome outcome) =>
        Summary?.Counts.SingleOrDefault(c => c.Kind == kind && c.Outcome == outcome)?.Count ?? 0;

    public int Count(DraftOutcome outcome) => Summary?.Counts.Where(c => c.Outcome == outcome).Sum(c => c.Count) ?? 0;
}
