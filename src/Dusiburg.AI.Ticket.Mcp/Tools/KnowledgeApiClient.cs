using System.Net;
using System.Net.Http.Json;
using Dusiburg.AI.Ticket.Mcp.Hosting;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Errors;
using Dusiburg.AI.Ticket.Shared.Knowledge;
using Dusiburg.AI.Ticket.Shared.Serialization;

namespace Dusiburg.AI.Ticket.Mcp.Tools;

/// <summary>
/// <c>/api/knowledge</c> dell'Agent (risorsa <c>agent</c> dell'AppHost, service discovery). Un 404 o un 400 dell'API diventano
/// errori strutturati del tool, con il messaggio dell'API: il modello li legge e può correggersi.
/// </summary>
public sealed class KnowledgeApiClient(HttpClient http)
{
    public const string BaseAddressConfigurationKey = "Agent:Url";
    public const string DefaultBaseAddress = "http://agent";

    public Task<KnowledgeSearchResult> SearchAsync(string text, string? product, DeploymentMode? mode, string? version, int max, CancellationToken cancellationToken) =>
        GetAsync<KnowledgeSearchResult>(Query("api/knowledge/search",
            ("text", text), ("product", product), ("mode", mode?.ToString()), ("version", version), ("max", Number(max))), cancellationToken);

    public Task<KnowledgeDocument> GetSourceAsync(string key, CancellationToken cancellationToken) =>
        GetAsync<KnowledgeDocument>($"api/knowledge/sources/{Uri.EscapeDataString(key)}", cancellationToken);

    public Task<List<ReleaseNoteItem>> ReleaseNotesAsync(string? product, string? from, string? to, CancellationToken cancellationToken) =>
        GetAsync<List<ReleaseNoteItem>>(Query("api/knowledge/release-notes", ("product", product), ("from", from), ("to", to)), cancellationToken);

    public Task<CustomerHistory> CustomerTicketsAsync(string? email, int max, CancellationToken cancellationToken) =>
        GetAsync<CustomerHistory>(Query("api/knowledge/customer-tickets", ("email", email), ("max", Number(max))), cancellationToken);

    /// <summary>Percorso con i soli parametri valorizzati: un parametro vuoto non vale come "non indicato" per ogni tipo.</summary>
    internal static string Query(string path, params (string Name, string? Value)[] parameters)
    {
        string[] pairs = [.. parameters.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!.Trim())}")];

        return pairs.Length == 0 ? path : $"{path}?{string.Join('&', pairs)}";
    }

    private static string Number(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(path, cancellationToken);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            ProblemBody? problem = await response.Content.ReadFromJsonAsync<ProblemBody>(TicketJson.Options, cancellationToken);

            throw new ToolException(response.StatusCode == HttpStatusCode.NotFound ? ErrorCodes.NotFound : ErrorCodes.ValidationError,
                problem?.Detail ?? problem?.Title ?? response.ReasonPhrase ?? "Richiesta non valida.");
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(TicketJson.Options, cancellationToken)
            ?? throw new ToolException(ErrorCodes.Internal, "Risposta vuota dall'Agent.");
    }

    private sealed record ProblemBody(string? Title, string? Detail);
}
