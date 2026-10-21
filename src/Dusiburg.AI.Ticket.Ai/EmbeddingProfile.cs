namespace Dusiburg.AI.Ticket.Ai;

/// <summary>
/// Prefissi e dimensione nativa di un modello di embedding, come <c>EmbeddingProfile</c> di Sinistri. I modelli instruction-aware
/// vogliono prefissi diversi per query e documento: senza, la qualità cala in modo evidente (banco di prova della Fase 1b di
/// Sinistri). Un modello non in tabella è un errore: non si usa un modello instruction-aware "senza profilo".
/// </summary>
public sealed record EmbeddingProfile(string Model, string QueryPrefix, string DocumentPrefix, int NativeDimensions)
{
    /// <summary>Istruzione per i modelli "Instruct/Query" (Qwen3-Embedding, e5-instruct): da tarare se si passa a uno di loro.</summary>
    private const string Instruct =
        "Instruct: Dato il testo di un ticket di assistenza, trova i ticket risolti, gli articoli della knowledge base e le note di rilascio pertinenti\nQuery: ";

    /// <summary>Profili noti: la chiave è il nome del modello senza tag (<c>embeddinggemma:latest</c> → <c>embeddinggemma</c>).</summary>
    private static readonly IReadOnlyList<(string Name, string? Tag, EmbeddingProfile Profile)> Known =
    [
        ("embeddinggemma", null, new("embeddinggemma", "task: search result | query: ", "title: none | text: ", 768)),
        ("bge-m3", null, new("bge-m3", "", "", 1024)),
        ("qwen3-embedding", "4b", new("qwen3-embedding:4b", Instruct, "", 2560)),
        ("qwen3-embedding", "8b", new("qwen3-embedding:8b", Instruct, "", 4096)),
        ("qwen3-embedding", null, new("qwen3-embedding:0.6b", Instruct, "", 1024)),
        ("multilingual-e5-large-instruct", null, new("multilingual-e5-large-instruct", Instruct, "", 1024)),
    ];

    public static EmbeddingProfile ForModel(string model)
    {
        string[] parts = model.Trim().Split(':', 2);
        string name = parts[0].Split('/').Last();
        string? tag = parts.Length > 1 ? parts[1] : null;

        return Known
            .Where(known => name.Equals(known.Name, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(known => known.Tag is null || string.Equals(known.Tag, tag, StringComparison.OrdinalIgnoreCase))
            .Profile
            ?? throw new InvalidOperationException(
                $"Modello di embedding '{model}' senza profilo (prefissi e dimensione): modelli supportati " +
                $"{string.Join(", ", Known.Select(known => known.Profile.Model).Distinct())}. Aggiungerlo a EmbeddingProfile.");
    }

    public string Query(string text) => QueryPrefix + text;

    public string Document(string text) => DocumentPrefix + text;
}
