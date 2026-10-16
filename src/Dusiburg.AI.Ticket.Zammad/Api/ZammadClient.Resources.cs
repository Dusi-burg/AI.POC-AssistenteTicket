using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Dusiburg.AI.Ticket.Zammad.Api;

/// <summary>
/// Operazioni generiche sulle risorse (elenco, creazione, modifica) e quelle con un percorso proprio (token, migrazioni,
/// impostazioni). Con <c>onBehalfOf</c> la richiesta porta l'header <c>From</c> (login o email): Zammad la esegue come
/// quell'utente se il token è di un amministratore (<c>X-On-Behalf-Of</c> è deprecato dalla 7).
/// </summary>
public sealed partial class ZammadClient
{
    public const int PageSize = 100;

    private const string FromHeader = "From";

    /// <summary>Tutte le pagine di una risorsa paginata (<c>page</c> + <c>per_page</c>).</summary>
    public async Task<IReadOnlyList<T>> ListAllAsync<T>(string resource, CancellationToken cancellationToken)
    {
        List<T> all = [];

        for (int page = 1; ; page++)
        {
            List<T> items = await GetAsync<List<T>>($"{resource}?page={page}&per_page={PageSize}", cancellationToken);
            all.AddRange(items);

            if (items.Count < PageSize)
            {
                return all;
            }
        }
    }

    /// <summary>Risorsa senza paginazione (attributi, tag, impostazioni, webhook, trigger) o elemento singolo.</summary>
    public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken, string? onBehalfOf = null) =>
        await SendAsync<T>(HttpMethod.Get, path, body: null, onBehalfOf, cancellationToken);

    public async Task<T> CreateAsync<T>(string resource, object body, CancellationToken cancellationToken, string? onBehalfOf = null) =>
        await SendAsync<T>(HttpMethod.Post, resource, body, onBehalfOf, cancellationToken);

    public async Task<T> UpdateAsync<T>(string resource, long id, object body, CancellationToken cancellationToken) =>
        await SendAsync<T>(HttpMethod.Put, $"{resource}/{id}", body, onBehalfOf: null, cancellationToken);

    public async Task CreateTagAsync(string name, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Post, ZammadResources.Tags, new { Name = name }, onBehalfOf: null, cancellationToken);

    /// <summary>Applica gli attributi creati o modificati: Zammad aggiunge le colonne e riavvia railsserver, scheduler e websocket.</summary>
    public async Task ExecuteObjectMigrationsAsync(CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Post, "object_manager_attributes_execute_migrations", new { }, onBehalfOf: null, cancellationToken);

    public async Task SetSettingAsync(long id, object value, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Put, $"{ZammadResources.Settings}/{id}", new { StateCurrent = new { Value = value } }, onBehalfOf: null, cancellationToken);

    /// <summary>Crea un token personale dell'utente <paramref name="onBehalfOf"/>; il valore si vede solo qui.</summary>
    public async Task<string> CreateAccessTokenAsync(string name, IReadOnlyList<string> permissions, string onBehalfOf, CancellationToken cancellationToken)
    {
        ZammadCreatedToken created = await SendAsync<ZammadCreatedToken>(
            HttpMethod.Post, "user_access_token", new { Name = name, Permission = permissions, ExpiresAt = (DateTimeOffset?)null }, onBehalfOf, cancellationToken);

        return created.Value;
    }

    public async Task<IReadOnlyList<ZammadAccessToken>> ListAccessTokensAsync(string onBehalfOf, CancellationToken cancellationToken) =>
        (await SendAsync<ZammadAccessTokenList>(HttpMethod.Get, "user_access_token", body: null, onBehalfOf, cancellationToken)).Tokens;

    public async Task DeleteAccessTokenAsync(long id, string onBehalfOf, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Delete, $"user_access_token/{id}", body: null, onBehalfOf, cancellationToken);

    /// <summary>Utente di un token diverso da quello del client (per riconoscere il token già salvato negli user-secrets).</summary>
    public async Task<ZammadUser> GetUserOfTokenAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/users/me?expand=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", $"token={token}");

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);

        await ZammadApiException.ThrowIfErrorAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<ZammadUser>(Json, cancellationToken)
            ?? throw new ZammadApiException(response.StatusCode, "risposta vuota da users/me");
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, string? onBehalfOf, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendCoreAsync(method, path, body, onBehalfOf, cancellationToken);

        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new ZammadApiException(response.StatusCode, $"risposta vuota da {path}");
    }

    private async Task SendAsync(HttpMethod method, string path, object? body, string? onBehalfOf, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendCoreAsync(method, path, body, onBehalfOf, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, object? body, string? onBehalfOf, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"api/v1/{path}");

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        if (onBehalfOf is not null)
        {
            request.Headers.Add(FromHeader, onBehalfOf);
        }

        HttpResponseMessage response = await http.SendAsync(request, cancellationToken);

        try
        {
            await ZammadApiException.ThrowIfErrorAsync(response, cancellationToken);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        return response;
    }
}
