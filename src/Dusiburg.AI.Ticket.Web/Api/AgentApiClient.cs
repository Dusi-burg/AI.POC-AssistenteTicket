using System.Net;
using System.Net.Http.Json;
using Dusiburg.AI.Ticket.Shared.Catalog;
using Dusiburg.AI.Ticket.Shared.Health;
using Dusiburg.AI.Ticket.Shared.Runs;
using Dusiburg.AI.Ticket.Shared.Serialization;

namespace Dusiburg.AI.Ticket.Web.Api;

/// <summary>Client tipizzato verso le API di vista dell'Agent, unico proprietario dei dati (PLAN §4).</summary>
public sealed class AgentApiClient(HttpClient http)
{
    /// <summary>Indirizzo da service discovery: l'AppHost dichiara la risorsa con il nome <c>agent</c>.</summary>
    public const string DefaultBaseAddress = "http://agent";

    /// <summary>Sovrascrive l'indirizzo, ad esempio nei test.</summary>
    public const string BaseAddressConfigurationKey = "Agent:BaseAddress";

    public async Task<ProbeReport> GetHealthAsync(CancellationToken cancellationToken) =>
        await GetAsync<ProbeReport>("/api/health", cancellationToken);

    /// <summary>Le ultime elaborazioni, con i filtri non nulli.</summary>
    public async Task<IReadOnlyList<RunSummary>> GetRunsAsync(RunFilter filter, CancellationToken cancellationToken)
    {
        string[] query =
        [
            .. filter.State is { } state ? [$"state={state}"] : Array.Empty<string>(),
            .. filter.Kind is { } kind ? [$"kind={kind}"] : Array.Empty<string>(),
            .. filter.Outcome is { } outcome ? [$"outcome={outcome}"] : Array.Empty<string>()
        ];

        return await GetAsync<List<RunSummary>>(query.Length == 0 ? "/api/runs" : $"/api/runs?{string.Join('&', query)}", cancellationToken);
    }

    /// <summary>Dettaglio di un'elaborazione; <c>null</c> se non esiste.</summary>
    public async Task<RunDetail?> GetRunAsync(int id, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync($"/api/runs/{id}", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<RunDetail>(TicketJson.Options, cancellationToken);
    }

    public async Task<OutcomeSummary> GetOutcomesAsync(CancellationToken cancellationToken) =>
        await GetAsync<OutcomeSummary>("/api/outcomes", cancellationToken);

    public async Task<CatalogView> GetCatalogAsync(CancellationToken cancellationToken) =>
        await GetAsync<CatalogView>("/api/catalog", cancellationToken);

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(path, cancellationToken);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(TicketJson.Options, cancellationToken)
            ?? throw new HttpRequestException($"Risposta vuota da {path} dell'Agent.");
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

    /// <summary>Il ticket nell'interfaccia di Zammad; <c>null</c> senza l'indirizzo.</summary>
    public string? Ticket(int zammadTicketId) =>
        ZammadUrl is { Length: > 0 } zammad ? $"{zammad.TrimEnd('/')}/#ticket/zoom/{zammadTicketId}" : null;

    /// <summary>La traccia di un'elaborazione nel dashboard di Aspire; <c>null</c> senza l'indirizzo o la traccia.</summary>
    public string? Trace(string? traceId) =>
        DashboardUrl is { Length: > 0 } dashboard && traceId is { Length: > 0 } ? $"{dashboard.TrimEnd('/')}/traces/detail/{traceId}" : null;
}
