using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Dusiburg.AI.Ticket.Core;

/// <summary>Sorgente degli eventi di Zammad (P1).</summary>
public enum TicketEventSourceKind
{
    Webhook,
    Polling
}

/// <summary>Manopole della pipeline (PLAN §8): soglia della confidenza, sorgente degli eventi, soglia del feedback.</summary>
/// <param name="ConfidenceMinForDraft">Sotto questa soglia la bozza diventa una richiesta di chiarimenti o un'escalation (P6).</param>
/// <param name="DashboardUrl">Indirizzo del dashboard di Aspire per il link alla traccia nella nota interna; nullo fuori dall'AppHost.</param>
/// <param name="FeedbackMaxEditDistance">
/// Oltre questa distanza di modifica la risposta dell'operatore non nasce dalla bozza: l'esito è <c>Discarded</c> (Fase 7).
/// </param>
public sealed record AgentOptions(
    double ConfidenceMinForDraft, TicketEventSourceKind EventSource, TimeSpan PollingInterval, Uri? DashboardUrl, double FeedbackMaxEditDistance)
{
    public const double DefaultConfidenceMinForDraft = 0.55;
    public const int DefaultPollingIntervalSeconds = 5;
    public const double DefaultFeedbackMaxEditDistance = 0.7;

    public static AgentOptions Default { get; } = new(
        DefaultConfidenceMinForDraft, TicketEventSourceKind.Webhook, TimeSpan.FromSeconds(DefaultPollingIntervalSeconds), null, DefaultFeedbackMaxEditDistance);

    public static class Keys
    {
        public const string ConfidenceMinForDraft = "CONFIDENCE_MIN_FOR_DRAFT";
        public const string EventSource = "TICKET_EVENT_SOURCE";
        public const string PollingInterval = "POLLING_INTERVAL_SECONDS";
        public const string DashboardUrl = "TICKET_DASHBOARD_URL";
        public const string FeedbackMaxEditDistance = "FEEDBACK_MAX_EDIT_DISTANCE";

        public static IReadOnlyList<string> All => [ConfidenceMinForDraft, EventSource, PollingInterval, FeedbackMaxEditDistance];
    }

    public static AgentOptions FromConfiguration(IConfiguration configuration) => new(
        ReadFraction(configuration, Keys.ConfidenceMinForDraft, DefaultConfidenceMinForDraft),
        ReadEventSource(configuration),
        TimeSpan.FromSeconds(ReadInt(configuration, Keys.PollingInterval, DefaultPollingIntervalSeconds, minimum: 1, maximum: 300)),
        Uri.TryCreate(configuration[Keys.DashboardUrl], UriKind.Absolute, out Uri? dashboard) ? dashboard : null,
        ReadFraction(configuration, Keys.FeedbackMaxEditDistance, DefaultFeedbackMaxEditDistance));

    /// <summary>Un numero fra 0 e 1 con il punto decimale.</summary>
    private static double ReadFraction(IConfiguration configuration, string key, double defaultValue)
    {
        string? value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double fraction) && fraction is >= 0 and <= 1
            ? fraction
            : throw Invalid($"{key} '{value}' non valido: serve un numero fra 0 e 1 con il punto decimale.");
    }

    private static TicketEventSourceKind ReadEventSource(IConfiguration configuration)
    {
        string? value = configuration[Keys.EventSource];

        if (string.IsNullOrWhiteSpace(value))
        {
            return TicketEventSourceKind.Webhook;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: true, out TicketEventSourceKind kind) && Enum.IsDefined(kind)
            ? kind
            : throw Invalid($"{Keys.EventSource} '{value}' non supportato: usare webhook oppure polling.");
    }

    private static int ReadInt(IConfiguration configuration, string key, int defaultValue, int minimum, int maximum)
    {
        string? value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number >= minimum && number <= maximum
            ? number
            : throw Invalid($"{key} '{value}' non valido: serve un intero tra {minimum} e {maximum}.");
    }

    private static InvalidOperationException Invalid(string message) => new($"Configurazione non valida: {message}");
}
