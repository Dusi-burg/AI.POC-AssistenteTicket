using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Zammad;
using Microsoft.Extensions.Configuration;

namespace Dusiburg.AI.Ticket.Cli.Configuration;

/// <summary>
/// Cli lanciata fuori dall'AppHost: connection string, token e manopole restano in un solo posto, gli user-secrets dell'AppHost
/// (come la Cli di Sinistri). Si aggiungono solo i valori non già presenti (variabili d'ambiente vincono), più il collegamento
/// OTLP al dashboard se l'AppHost è in esecuzione.
/// </summary>
internal static class CliConfiguration
{
    /// <summary>Token amministratore esplicito, per lanciare la Cli senza user-secrets.</summary>
    public const string AdminTokenVariable = "ZAMMAD_ADMIN_TOKEN";

    /// <summary>Endpoint OTLP del dashboard (profilo https dell'AppHost).</summary>
    public const string DashboardOtlpEndpoint = "https://localhost:21358";

    public static void AddAppHostSecrets(this IConfigurationManager configuration, UserSecretsStore secrets)
    {
        var values = new Dictionary<string, string?>();

        void AddIfMissing(string key, string? value)
        {
            if (string.IsNullOrEmpty(configuration[key]) && !string.IsNullOrEmpty(value))
            {
                values[key] = value;
            }
        }

        AddIfMissing(UserSecretsStore.SqlConnectionStringKey, secrets.Get(UserSecretsStore.SqlConnectionStringKey));

        // La Cli lavora con il token amministratore; al primo bootstrap è ancora quello sotto la chiave dell'Agent.
        AddIfMissing(ZammadOptions.Keys.ApiToken,
            configuration[AdminTokenVariable] ?? secrets.Get(UserSecretsStore.AdminTokenKey) ?? secrets.Get(UserSecretsStore.ApiTokenKey));

        foreach (string key in AiOptions.Keys.All.Append(ZammadOptions.Keys.BaseUrl).Append(ZammadOptions.Keys.WebhookTarget))
        {
            AddIfMissing(key, secrets.Get(key));
        }

        if (string.IsNullOrEmpty(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]) && secrets.Get("AppHost:OtlpApiKey") is { } otlpApiKey)
        {
            values["OTEL_EXPORTER_OTLP_ENDPOINT"] = DashboardOtlpEndpoint;
            values["OTEL_EXPORTER_OTLP_HEADERS"] = $"x-otlp-api-key={otlpApiKey}";
            values["OTEL_SERVICE_NAME"] = "ticket-cli";
        }

        configuration.AddInMemoryCollection(values);
    }
}
