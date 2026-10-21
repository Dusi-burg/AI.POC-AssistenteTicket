using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dusiburg.AI.Ticket.ServiceDefaults.Problems;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Zammad;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Dusiburg.AI.Ticket.Agent.Events;

/// <summary>Esito di una consegna del webhook, restituito a Zammad (che lo mostra nel registro delle consegne).</summary>
/// <param name="Resolved">Bozze di cui la consegna ha registrato l'esito (risposta dell'operatore o chiusura).</param>
public sealed record WebhookAccepted(string Outcome, int? RunId, int Resolved);

/// <summary>
/// Sorgente webhook (P1): <c>POST /webhooks/zammad</c> dai trigger "articolo del cliente", "risposta dell'operatore" e "ticket
/// chiuso", firmato HMAC-SHA1 nell'header <c>X-Hub-Signature: sha1=&lt;hex&gt;</c> con <c>ZAMMAD_WEBHOOK_SECRET</c> (fase-0.md
/// §4 ter). Risponde <c>202</c>: un'elaborazione va in coda e Zammad non aspetta il modello (timeout del job di 30 s); il
/// controllo dell'esito si fa nella richiesta (~1 s) e un suo errore torna a Zammad, che ripete la consegna.
/// </summary>
public static class WebhookEventSource
{
    public const string SignatureHeader = "X-Hub-Signature";
    public const string DeliveryHeader = "X-Zammad-Delivery";
    public const string TriggerHeader = "X-Zammad-Trigger";

    public static IEndpointRouteBuilder MapZammadWebhook(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/webhooks/zammad", ReceiveAsync).ExcludeFromDescription();

        return endpoints;
    }

    internal static async Task<Results<Accepted<WebhookAccepted>, ProblemHttpResult>> ReceiveAsync(
        HttpRequest request, IConfiguration configuration, TicketEventIntake intake, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        string? secret = configuration[ZammadOptions.Keys.WebhookSecret];

        if (string.IsNullOrWhiteSpace(secret))
        {
            return ApiProblems.UpstreamUnavailable($"{ZammadOptions.Keys.WebhookSecret} non configurato: eseguire 'zammad bootstrap' e riavviare l'AppHost.");
        }

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        byte[] body = buffer.ToArray();

        if (!IsSignatureValid(body, request.Headers[SignatureHeader].ToString(), secret))
        {
            return ApiProblems.Unauthorized($"Firma {SignatureHeader} assente o non valida.");
        }

        if (!TryParse(body, out TicketEvent? ticketEvent))
        {
            return ApiProblems.Validation("Payload non riconosciuto: servono ticket_id e article_id (con article_sender e article_internal) oppure ticket_state.");
        }

        IntakeResult result = await intake.AcceptAsync(ticketEvent, cancellationToken);
        loggers.CreateLogger(typeof(WebhookEventSource)).LogInformation("Consegna {Delivery} del webhook ({Trigger}): {Outcome}",
            request.Headers[DeliveryHeader].ToString(), request.Headers[TriggerHeader].ToString(), result.Outcome);

        return TypedResults.Accepted((string?)null, new WebhookAccepted(result.Outcome.ToString(), result.RunId, result.Resolved?.Count ?? 0));
    }

    /// <summary>HMAC-SHA1 del corpo grezzo, confrontato in tempo costante.</summary>
    public static bool IsSignatureValid(byte[] body, string header, string secret)
    {
        const string prefix = "sha1=";

        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] expected = HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), body);

        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(header[prefix.Length..].Trim()));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string Sign(byte[] body, string secret) =>
        "sha1=" + Convert.ToHexStringLower(HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), body));

    /// <summary>
    /// Payload minimale di <c>zammad bootstrap</c>. Le variabili di Zammad arrivano col tipo nativo (<c>3</c>, <c>false</c>), ma
    /// si accettano anche come stringhe. Il trigger di chiusura non ha articolo: <c>article_id</c> non è un numero e resta nullo,
    /// basta <c>ticket_state</c>.
    /// </summary>
    internal static bool TryParse(byte[] body, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TicketEvent? ticketEvent)
    {
        ticketEvent = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            long? articleId = ReadLong(root, "article_id");
            string? state = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ticket_state", out JsonElement t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            if (ReadLong(root, "ticket_id") is not { } ticketId || (articleId is null && string.IsNullOrWhiteSpace(state)))
            {
                return false;
            }

            string? sender = articleId is not null && root.TryGetProperty("article_sender", out JsonElement s) ? s.ToString() : null;
            bool isInternal = root.TryGetProperty("article_internal", out JsonElement i)
                && (i.ValueKind == JsonValueKind.True || (i.ValueKind == JsonValueKind.String && bool.TryParse(i.GetString(), out bool value) && value));

            ticketEvent = new TicketEvent(ticketId, articleId, sender, isInternal, RunTrigger.Webhook, state);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long? ReadLong(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt64(out long number) => number,
                JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) => number,
                _ => null
            }
            : null;
}
