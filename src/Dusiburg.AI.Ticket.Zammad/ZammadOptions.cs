using Microsoft.Extensions.Configuration;

namespace Dusiburg.AI.Ticket.Zammad;

/// <summary>
/// Indirizzo e token di Zammad (PLAN §8). Il token è quello dell'utente tecnico <c>ai-assistant</c> creato da
/// <c>zammad bootstrap</c> (Fase 4): arriva dall'AppHost (<c>Parameters:zammad-api-token</c> negli user-secrets).
/// </summary>
public sealed record ZammadOptions(Uri BaseUrl, string? ApiToken)
{
    /// <summary>Porta pubblicata da WSL su Windows solo su localhost (fase-0.md §4 bis).</summary>
    public const string DefaultBaseUrl = "http://127.0.0.1:8080";

    /// <summary>Windows visto da WSL e dai container (fase-0.md §1 bis): lì ascolta l'Agent.</summary>
    public const string DefaultWebhookTarget = "http://172.24.176.1:5401/webhooks/zammad";

    public static class Keys
    {
        public const string BaseUrl = "ZAMMAD_URL";
        public const string ApiToken = "ZAMMAD_API_TOKEN";

        /// <summary>Segreto HMAC del webhook (<c>Parameters:zammad-webhook-secret</c>, generato da <c>zammad bootstrap</c>).</summary>
        public const string WebhookSecret = "ZAMMAD_WEBHOOK_SECRET";

        /// <summary>Indirizzo scritto nel webhook da <c>zammad bootstrap</c>.</summary>
        public const string WebhookTarget = "ZAMMAD_WEBHOOK_TARGET";
    }

    public bool HasApiToken => !string.IsNullOrWhiteSpace(ApiToken);

    public static ZammadOptions FromConfiguration(IConfiguration configuration) => new(ReadBaseUrl(configuration), NonEmpty(configuration[Keys.ApiToken]));

    private static Uri ReadBaseUrl(IConfiguration configuration)
    {
        string value = NonEmpty(configuration[Keys.BaseUrl]) ?? DefaultBaseUrl;

        // La barra finale fa sì che i percorsi relativi ("api/v1/...") si aggiungano all'indirizzo invece di sostituirne l'ultimo segmento.
        return Uri.TryCreate(value.EndsWith('/') ? value : value + "/", UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https"
            ? uri
            : throw new InvalidOperationException($"Configurazione non valida: {Keys.BaseUrl} '{value}' non è un indirizzo http(s) valido.");
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
