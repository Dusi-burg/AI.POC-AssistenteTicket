namespace Dusiburg.AI.Ticket.AppHost;

/// <summary>
/// Nomi delle manopole inoltrate all'Agent. Copiati da <c>AiOptions.Keys</c> e <c>ZammadOptions.Keys</c> invece di referenziare
/// quei progetti: l'AppHost referenzia solo i servizi che avvia.
/// </summary>
internal static class ModelSettings
{
    public const string OllamaEndpointKey = "OLLAMA_ENDPOINT";

    public const string DefaultOllamaEndpoint = "http://127.0.0.1:11434";

    public static readonly string[] All =
    [
        OllamaEndpointKey,
        "OLLAMA_CHAT_MODEL",
        "OLLAMA_NUM_CTX",
        "OLLAMA_NUM_GPU",
        "ANTHROPIC_MODEL",
        "MODEL_CLASSIFY_PROVIDER",
        "MODEL_DRAFT_PROVIDER",
        "EMBEDDING_MODEL",
        "EMBEDDING_DIMENSIONS",
        "TICKET_PROMPT_CAPTURE_DIR"
    ];
}

/// <summary>Manopole del retrieval, copiate da <c>RetrievalOptions.Keys</c> (Fase 5).</summary>
internal static class RetrievalSettings
{
    public static readonly string[] All = ["RETRIEVAL_TOP_K", "PROMPT_SOURCES_MAX", "RRF_K", "RETRIEVAL_VERSION_PENALTY"];
}

internal static class ZammadSettings
{
    public const string BaseUrlKey = "ZAMMAD_URL";

    public const string ApiTokenKey = "ZAMMAD_API_TOKEN";

    public const string DefaultBaseUrl = "http://127.0.0.1:8080";
}

internal static class AppHostExtensions
{
    /// <summary>Qualcuno ascolta già a quell'indirizzo? Una prova di connessione breve, fatta una volta all'avvio.</summary>
    public static bool IsListening(Uri endpoint)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();

            return client.ConnectAsync(endpoint.Host, endpoint.Port).Wait(TimeSpan.FromSeconds(1)) && client.Connected;
        }
        catch (Exception exception) when (exception is AggregateException or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Inoltra al servizio le impostazioni presenti nella configurazione dell'AppHost, saltando quelle non valorizzate:
    /// così una variabile della demo si imposta in un solo posto e non va replicata in ogni progetto.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithConfigurationEnvironment(
        this IResourceBuilder<ProjectResource> resource, IDistributedApplicationBuilder builder, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            // Nelle variabili d'ambiente la sezione si scrive con il doppio underscore; nella configurazione con i due punti.
            if (builder.Configuration[name.Replace("__", ":", StringComparison.Ordinal)] is { Length: > 0 } value)
            {
                resource.WithEnvironment(name, value);
            }
        }

        return resource;
    }
}
