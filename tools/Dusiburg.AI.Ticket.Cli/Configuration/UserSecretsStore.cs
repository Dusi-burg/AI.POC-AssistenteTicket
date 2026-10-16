using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace Dusiburg.AI.Ticket.Cli.Configuration;

/// <summary>
/// User-secrets dell'AppHost (<c>secrets.json</c>, chiavi piatte come <c>Parameters:zammad-api-token</c>): la Cli li legge per
/// la propria configurazione e <c>zammad bootstrap</c> ci scrive token e segreto del webhook (decisione dell'utente, fase-4.md).
/// Le altre chiavi restano come sono; i valori non vengono mai stampati.
/// </summary>
internal sealed class UserSecretsStore(string path)
{
    /// <summary><c>UserSecretsId</c> di <c>Dusiburg.AI.Ticket.AppHost.csproj</c>.</summary>
    public const string AppHostUserSecretsId = "29c88890-cc3f-4090-82fc-dd4e2ecf92e5";

    public const string SqlConnectionStringKey = "ConnectionStrings:sql";

    /// <summary>Token dell'utente tecnico <c>ai-assistant</c>, passato all'Agent come <c>ZAMMAD_API_TOKEN</c>.</summary>
    public const string ApiTokenKey = "Parameters:zammad-api-token";

    /// <summary>Token di un amministratore: lo usa solo la Cli (bootstrap e seed).</summary>
    public const string AdminTokenKey = "Parameters:zammad-admin-token";

    public const string WebhookSecretKey = "Parameters:zammad-webhook-secret";

    public string Path { get; } = path;

    public static UserSecretsStore ForAppHost() => new(PathHelper.GetSecretsPathFromSecretsId(AppHostUserSecretsId));

    public string? Get(string key) => Read()[key]?.GetValue<string>() is { Length: > 0 } value ? value : null;

    public void Set(IReadOnlyDictionary<string, string> values)
    {
        JsonObject secrets = Read();

        foreach ((string key, string value) in values)
        {
            secrets[key] = value;
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        // Scrittura su un file temporaneo e poi sostituzione: un'interruzione non lascia un secrets.json a metà.
        string temporary = Path + ".tmp";
        File.WriteAllText(temporary, secrets.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, Path, overwrite: true);
    }

    private JsonObject Read() =>
        File.Exists(Path) && JsonNode.Parse(File.ReadAllText(Path)) is JsonObject secrets ? secrets : [];
}
