using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Dusiburg.AI.Ticket.Zammad.Api;

/// <summary>
/// Client tipizzato dell'API REST di Zammad (<c>/api/v1</c>), con il token nell'header <c>Authorization: Token token=…</c>
/// impostato alla registrazione (<see cref="ZammadServiceCollectionExtensions"/>). La resilienza standard viene da ServiceDefaults.
/// </summary>
public sealed partial class ZammadClient(HttpClient http)
{
    /// <summary>Zammad usa lo snake_case (<c>first_response_at</c>, <c>ticket_id</c>).</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>
    /// La pagina iniziale risponde senza autenticazione: basta a dire che lo stack è su e nginx raggiunge il railsserver. Va chiesta
    /// come HTML: con l'<c>Accept: application/json</c> di default del client Zammad risponde 500 (<c>ActionController::UnknownFormat</c>).
    /// </summary>
    public async Task<HttpStatusCode> PingAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);

        return response.StatusCode;
    }

    /// <summary>Utente a cui appartiene il token. Un token assente o non valido è un <see cref="ZammadApiException"/> con 401.</summary>
    public async Task<ZammadUser> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync("api/v1/users/me?expand=true", cancellationToken);

        await ZammadApiException.ThrowIfErrorAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ZammadUser>(Json, cancellationToken)
            ?? throw new ZammadApiException(response.StatusCode, "risposta vuota da users/me");
    }
}

/// <summary>Risposta di errore dell'API di Zammad, con il messaggio che Zammad mette nel campo <c>error</c>.</summary>
public sealed class ZammadApiException(HttpStatusCode statusCode, string message) : Exception($"Zammad {(int)statusCode}: {message}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public static async Task ThrowIfErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        string message = body;

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                message = error.ToString();
            }
        }
        catch (JsonException)
        {
            // Corpo non JSON (per esempio la pagina 502 di nginx mentre il railsserver riparte): resta il testo.
        }

        throw new ZammadApiException(response.StatusCode, string.IsNullOrWhiteSpace(message) ? response.ReasonPhrase ?? "errore" : message.Trim());
    }
}
