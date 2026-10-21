using Dusiburg.AI.Ticket.Shared.Health;
using Dusiburg.AI.Ticket.Web.Api;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Dusiburg.AI.Ticket.Web.Pages;

/// <summary>
/// Stato: i controlli di <c>/api/health</c> dell'Agent e i link a Zammad e al dashboard. Un Agent fermo non rompe la pagina:
/// si mostra il motivo, perché fa parte di ciò che la console deve far vedere.
/// </summary>
public sealed class IndexModel(AgentApiClient agent, IOptions<ConsoleLinks> links) : PageModel
{
    public ProbeReport? Report { get; private set; }

    public string? Error { get; private set; }

    public ConsoleLinks Links => links.Value;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            Report = await agent.GetHealthAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Error = $"Agent non raggiungibile: {exception.Message}";
        }
    }
}
