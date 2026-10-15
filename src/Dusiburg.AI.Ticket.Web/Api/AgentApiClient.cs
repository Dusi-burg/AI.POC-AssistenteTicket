using System.Net.Http.Json;
using Dusiburg.AI.Ticket.Shared.Health;
using Dusiburg.AI.Ticket.Shared.Serialization;

namespace Dusiburg.AI.Ticket.Web.Api;

/// <summary>Client tipizzato verso le API di vista dell'Agent, unico proprietario dei dati (PLAN §4).</summary>
public sealed class AgentApiClient(HttpClient http)
{
    /// <summary>Indirizzo da service discovery: l'AppHost dichiara la risorsa con il nome <c>agent</c>.</summary>
    public const string DefaultBaseAddress = "http://agent";

    /// <summary>Sovrascrive l'indirizzo, ad esempio nei test.</summary>
    public const string BaseAddressConfigurationKey = "Agent:BaseAddress";

    public async Task<ProbeReport> GetHealthAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync("/api/health", cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ProbeReport>(TicketJson.Options, cancellationToken)
            ?? throw new HttpRequestException("Risposta vuota da /api/health dell'Agent.");
    }
}

/// <summary>Link esterni mostrati dalla console: arrivano dall'AppHost come variabili d'ambiente.</summary>
public sealed class ConsoleLinks
{
    public const string ZammadUrlKey = "ZAMMAD_URL";

    public const string DashboardUrlKey = "TICKET_DASHBOARD_URL";

    /// <summary>Indirizzo di Zammad visto dal browser (stesso valore di <c>ZAMMAD_URL</c> dell'Agent).</summary>
    public string? ZammadUrl { get; set; }

    /// <summary>Dashboard di Aspire, per aprire tracce e log.</summary>
    public string? DashboardUrl { get; set; }
}
