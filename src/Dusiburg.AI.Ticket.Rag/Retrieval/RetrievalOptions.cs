using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Dusiburg.AI.Ticket.Rag.Retrieval;

/// <summary>
/// Manopole del retrieval (PLAN §8): si impostano sull'AppHost e arrivano all'Agent; la Cli le legge dagli user-secrets.
/// </summary>
/// <param name="TopK">Chunk chiesti a ciascuna delle due ricerche e tenuti dopo la fusione.</param>
/// <param name="PromptSourcesMax">Fonti distinte che entrano nel prompt (Fase 6).</param>
/// <param name="RrfK">Costante della Reciprocal Rank Fusion: punteggio = Σ 1 / (k + rank).</param>
/// <param name="VersionPenalty">Moltiplicatore del punteggio di un chunk il cui intervallo di versioni non contiene quella del cliente.</param>
public sealed record RetrievalOptions(int TopK, int PromptSourcesMax, int RrfK, double VersionPenalty)
{
    public const int DefaultTopK = 20;
    public const int DefaultPromptSourcesMax = 4;
    public const int DefaultRrfK = 60;
    public const double DefaultVersionPenalty = 0.5;

    public static RetrievalOptions Default { get; } = new(DefaultTopK, DefaultPromptSourcesMax, DefaultRrfK, DefaultVersionPenalty);

    public static class Keys
    {
        public const string TopK = "RETRIEVAL_TOP_K";
        public const string PromptSourcesMax = "PROMPT_SOURCES_MAX";
        public const string RrfK = "RRF_K";
        public const string VersionPenalty = "RETRIEVAL_VERSION_PENALTY";

        public static IReadOnlyList<string> All => [TopK, PromptSourcesMax, RrfK, VersionPenalty];
    }

    public static RetrievalOptions FromConfiguration(IConfiguration configuration) => new(
        ReadInt(configuration, Keys.TopK, DefaultTopK, minimum: 1, maximum: 200),
        ReadInt(configuration, Keys.PromptSourcesMax, DefaultPromptSourcesMax, minimum: 1, maximum: 10),
        ReadInt(configuration, Keys.RrfK, DefaultRrfK, minimum: 1, maximum: 1_000),
        ReadPenalty(configuration));

    private static int ReadInt(IConfiguration configuration, string key, int defaultValue, int minimum, int maximum)
    {
        string? value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number >= minimum && number <= maximum
            ? number
            : throw new InvalidOperationException($"Configurazione non valida: {key} '{value}' non valido, serve un intero tra {minimum} e {maximum}.");
    }

    /// <summary>Fra 0 (esclusi) e 1 (nessuna penalità), con il punto decimale.</summary>
    private static double ReadPenalty(IConfiguration configuration)
    {
        string? value = configuration[Keys.VersionPenalty];

        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultVersionPenalty;
        }

        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double penalty) && penalty > 0 && penalty <= 1
            ? penalty
            : throw new InvalidOperationException(
                $"Configurazione non valida: {Keys.VersionPenalty} '{value}' non valido, serve un numero fra 0 (escluso) e 1 con il punto decimale.");
    }
}
