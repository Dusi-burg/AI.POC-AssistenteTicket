using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Dusiburg.AI.Ticket.Ai;

/// <summary>
/// Manopole dei modelli (PLAN §8), come <c>AiOptions</c> di Trasporto: si impostano sull'AppHost (user-secrets, riga di comando,
/// variabili d'ambiente) e arrivano ai servizi; senza valore vale il default del codice.
/// </summary>
public sealed record AiOptions(
    Uri OllamaEndpoint,
    string OllamaChatModel,
    int OllamaContextLength,
    int? OllamaNumGpu,
    string? AnthropicApiKey,
    string AnthropicModel,
    IReadOnlyDictionary<ModelTask, string> TaskProviders,
    string EmbeddingModel,
    int EmbeddingDimensions,
    string? PromptCaptureDirectory)
{
    /// <summary>127.0.0.1 e non localhost: su Windows ogni nuova connessione a localhost prova prima IPv6 e perde ~2 s (misura di Sinistri).</summary>
    public const string DefaultOllamaEndpoint = "http://127.0.0.1:11434";

    public const string DefaultOllamaChatModel = "qwen3.5:9b";

    /// <summary>Istruzioni, ticket, contesto del cliente e 4 fonti stanno in ~2.300 token, più ~700 di risposta (fase-0.md §5).</summary>
    public const int DefaultOllamaContextLength = 8_192;

    /// <summary>
    /// Layer della chat in GPU (99 = tutti). Da Ollama 0.40 il runner tiene libero un margine di ~1,9 GB di VRAM e su 8 GB lascia un
    /// layer sulla CPU; con il numero esplicito qwen3.5:9b sta tutto in GPU (~+26% di token/s, fase-0.md §5, D21).
    /// </summary>
    public const int DefaultOllamaNumGpu = 99;

    /// <summary>Valore di <c>OLLAMA_NUM_GPU</c> che lascia la scelta a Ollama (margine automatico).</summary>
    public const string AutomaticNumGpu = "auto";

    /// <summary>Solo per il confronto della Fase 8 (G10, Q4).</summary>
    public const string DefaultAnthropicModel = "claude-haiku-5-5";

    public const string DefaultEmbeddingModel = "embeddinggemma";

    public const int DefaultEmbeddingDimensions = 768;

    /// <summary>Limite del tipo <c>VECTOR</c> di SQL Server 2025 in <c>float32</c>.</summary>
    public const int MaxEmbeddingDimensions = 1998;

    public static class Keys
    {
        public const string OllamaEndpoint = "OLLAMA_ENDPOINT";
        public const string OllamaChatModel = "OLLAMA_CHAT_MODEL";
        public const string OllamaNumCtx = "OLLAMA_NUM_CTX";
        public const string OllamaNumGpu = "OLLAMA_NUM_GPU";
        public const string AnthropicApiKey = "ANTHROPIC_API_KEY";
        public const string AnthropicModel = "ANTHROPIC_MODEL";
        public const string EmbeddingModel = "EMBEDDING_MODEL";
        public const string EmbeddingDimensions = "EMBEDDING_DIMENSIONS";
        public const string PromptCaptureDirectory = "TICKET_PROMPT_CAPTURE_DIR";

        /// <summary><c>MODEL_CLASSIFY_PROVIDER</c>, <c>MODEL_DRAFT_PROVIDER</c>.</summary>
        public static string ProviderFor(ModelTask task) => $"MODEL_{task.ToString().ToUpperInvariant()}_PROVIDER";

        /// <summary>Tutte le manopole dei modelli, per l'inoltro dall'AppHost.</summary>
        public static IReadOnlyList<string> All =>
        [
            OllamaEndpoint, OllamaChatModel, OllamaNumCtx, OllamaNumGpu, AnthropicModel, EmbeddingModel, EmbeddingDimensions,
            PromptCaptureDirectory, .. Enum.GetValues<ModelTask>().Select(ProviderFor)
        ];
    }

    /// <summary>Tutto in locale (G10): niente cloud a runtime, Anthropic solo se una manopola lo chiede.</summary>
    public const string DefaultProvider = ModelProviders.Ollama;

    public bool HasAnthropicApiKey => !string.IsNullOrWhiteSpace(AnthropicApiKey);

    public string ProviderFor(ModelTask task) => TaskProviders[task];

    public static AiOptions FromConfiguration(IConfiguration configuration) => new(
        ReadUri(configuration, Keys.OllamaEndpoint) ?? new Uri(DefaultOllamaEndpoint),
        NonEmpty(configuration[Keys.OllamaChatModel]) ?? DefaultOllamaChatModel,
        ReadInt(configuration, Keys.OllamaNumCtx, DefaultOllamaContextLength, minimum: 1_024, maximum: 262_144),
        ReadNumGpu(configuration),
        NonEmpty(configuration[Keys.AnthropicApiKey]),
        NonEmpty(configuration[Keys.AnthropicModel]) ?? DefaultAnthropicModel,
        Enum.GetValues<ModelTask>().ToDictionary(task => task, task => ReadProvider(configuration, task)),
        NonEmpty(configuration[Keys.EmbeddingModel]) ?? DefaultEmbeddingModel,
        ReadEmbeddingDimensions(configuration),
        NonEmpty(configuration[Keys.PromptCaptureDirectory]));

    /// <summary><c>EMBEDDING_DIMENSIONS</c> da sola: la usa anche chi crea la colonna <c>VECTOR</c> (<c>DbInit</c>).</summary>
    public static int ReadEmbeddingDimensions(IConfiguration configuration) =>
        ReadInt(configuration, Keys.EmbeddingDimensions, DefaultEmbeddingDimensions, minimum: 1, maximum: MaxEmbeddingDimensions);

    private static string ReadProvider(IConfiguration configuration, ModelTask task)
    {
        string key = Keys.ProviderFor(task);
        string provider = (NonEmpty(configuration[key]) ?? DefaultProvider).ToLowerInvariant();

        return ModelProviders.All.Contains(provider)
            ? provider
            : throw Invalid($"{key} '{provider}' non supportato: usare {string.Join(" oppure ", ModelProviders.All)}.");
    }

    private static int? ReadNumGpu(IConfiguration configuration)
    {
        string? value = NonEmpty(configuration[Keys.OllamaNumGpu]);

        if (value is null)
        {
            return DefaultOllamaNumGpu;
        }

        return value.Equals(AutomaticNumGpu, StringComparison.OrdinalIgnoreCase)
            ? null
            : ReadInt(configuration, Keys.OllamaNumGpu, DefaultOllamaNumGpu, minimum: 0, maximum: 999);
    }

    private static Uri? ReadUri(IConfiguration configuration, string key)
    {
        string? value = NonEmpty(configuration[key]);

        if (value is null)
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https"
            ? uri
            : throw Invalid($"{key} '{value}' non è un indirizzo http(s) valido.");
    }

    private static int ReadInt(IConfiguration configuration, string key, int defaultValue, int minimum, int maximum)
    {
        string? value = NonEmpty(configuration[key]);

        if (value is null)
        {
            return defaultValue;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number >= minimum && number <= maximum
            ? number
            : throw Invalid($"{key} '{value}' non valido: serve un intero tra {minimum} e {maximum}.");
    }

    private static InvalidOperationException Invalid(string message) => new($"Configurazione non valida: {message}");

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
