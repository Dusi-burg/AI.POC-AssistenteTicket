using Dusiburg.AI.Ticket.Shared.Runs;
using Dusiburg.AI.Ticket.Web.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Dusiburg.AI.Ticket.Web.Pages.Runs;

/// <summary>
/// Dettaglio di un'elaborazione: decisione e segnali della confidenza, fonti, tempi per passo, link a Zammad e alla traccia, e
/// la bozza accanto al testo inviato dall'operatore (Fase 7).
/// </summary>
public sealed class DetailsModel(AgentApiClient agent, IOptions<ConsoleLinks> links) : PageModel
{
    public RunDetail? Detail { get; private set; }

    public string? Error { get; private set; }

    public ConsoleLinks Links => links.Value;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        try
        {
            Detail = await agent.GetRunAsync(id, cancellationToken);

            if (Detail is null)
            {
                return NotFound();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Error = $"Agent non raggiungibile: {exception.Message}";
        }

        return Page();
    }

    /// <summary>Quota di un passo sul totale, per la barra dei tempi.</summary>
    public static int Share(int milliseconds, int total) => total == 0 ? 0 : (int)Math.Round(100.0 * milliseconds / total);
}
