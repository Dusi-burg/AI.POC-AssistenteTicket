namespace Dusiburg.AI.Ticket.Ai;

/// <summary>Compiti per cui si sceglie un modello (PLAN §8, <c>MODEL_&lt;COMPITO&gt;_PROVIDER</c>).</summary>
public enum ModelTask
{
    /// <summary>Classificazione del ticket: categoria, priorità, informazioni mancanti.</summary>
    Classify,

    /// <summary>Bozza di risposta con le fonti citate.</summary>
    Draft
}

public static class ModelProviders
{
    public const string Ollama = "ollama";

    public const string Anthropic = "anthropic";

    public static readonly IReadOnlyList<string> All = [Ollama, Anthropic];
}
