using Dusiburg.AI.Ticket.Shared.Catalog;
using Dusiburg.AI.Ticket.Web.Api;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Dusiburg.AI.Ticket.Web.Pages.Data;

/// <summary>Dati demo: prodotti e versioni, clienti, articoli della knowledge base e note di rilascio del "gestionale" simulato.</summary>
public sealed class IndexModel(AgentApiClient agent) : PageModel
{
    public CatalogView? Catalog { get; private set; }

    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            Catalog = await agent.GetCatalogAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Error = $"Agent non raggiungibile: {exception.Message}";
        }
    }
}
