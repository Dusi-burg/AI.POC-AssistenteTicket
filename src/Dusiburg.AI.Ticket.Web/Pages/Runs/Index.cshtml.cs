using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Runs;
using Dusiburg.AI.Ticket.Web.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Dusiburg.AI.Ticket.Web.Pages.Runs;

/// <summary>Elaborazioni: le ultime dell'Agent, con filtri in GET (l'indirizzo si salva e si riapre).</summary>
public sealed class IndexModel(AgentApiClient agent, IOptions<ConsoleLinks> links) : PageModel
{
    [BindProperty(SupportsGet = true)] public AgentRunState? State { get; set; }

    [BindProperty(SupportsGet = true)] public DraftKind? Kind { get; set; }

    [BindProperty(SupportsGet = true)] public DraftOutcome? Outcome { get; set; }

    public IReadOnlyList<RunSummary>? Runs { get; private set; }

    public string? Error { get; private set; }

    public ConsoleLinks Links => links.Value;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            Runs = await agent.GetRunsAsync(new RunFilter(State, Kind, Outcome), cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Error = $"Agent non raggiungibile: {exception.Message}";
        }
    }
}
