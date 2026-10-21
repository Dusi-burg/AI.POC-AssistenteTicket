using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Core.Context;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Core.Pipeline;

// Modalità agente (Fase 9b, estensione di G8): il modello sceglie le fonti con i tool del server MCP invece di ricevere quelle del
// retrieval fisso. Qui solo il contratto: l'implementazione (client MCP, guardia dei tool, Agent Framework) sta in
// Dusiburg.AI.Ticket.Agentic, così il nucleo non dipende né da MCP né dall'Agent Framework.

/// <summary>Un chunk di una fonte letta dall'agente; la distanza c'è se il chunk è uscito da una ricerca vettoriale.</summary>
public sealed record EvidenceChunk(int ChunkId, double? VectorDistance);

/// <summary>
/// Una fonte che l'agente ha davvero letto, con i chunk che ha visto: è l'unica base su cui la verifica accetta citazioni e termini
/// tecnici. Dalla ricerca vede l'estratto del chunk migliore (con la distanza); con <paramref name="Whole"/> (lettura completa, nota
/// di rilascio) vale la fonte intera, con le distanze note dei chunk già visti.
/// </summary>
public sealed record SourceEvidence(ChunkSource Source, string Key, double Score, IReadOnlyList<EvidenceChunk> Chunks, bool Whole = false)
{
    public bool IsWhole => Whole || Chunks.Count == 0;
}

/// <summary>Una chiamata a un tool: argomenti in JSON, esito (<c>ok</c>, <c>error:…</c>, <c>rejected:…</c>), durata, messaggio.</summary>
public sealed record ToolCallRecord(string ToolName, string Arguments, string Outcome, TimeSpan Duration, string? Message)
{
    public bool Succeeded => Outcome == "ok";
}

/// <summary>Che cosa ha prodotto l'agente: la bozza nello stesso contratto della pipeline fissa, le fonti lette, le chiamate.</summary>
public sealed record ToolAgentDraft(ModelCall<ModelDraft> Draft, IReadOnlyList<SourceEvidence> Evidence, IReadOnlyList<ToolCallRecord> ToolCalls);

public interface IToolAgentDrafter
{
    /// <summary>Il modello del compito <see cref="ModelTask.Agent"/> lavora con i tool e restituisce la bozza strutturata.</summary>
    Task<ToolAgentDraft> DraftAsync(IncomingTicket ticket, ModelClassification classification, CancellationToken cancellationToken);

    /// <summary>Lo stesso agente con un'altra fabbrica dei modelli: il confronto con il cloud della valutazione.</summary>
    IToolAgentDrafter WithModels(IChatClientFactory factory);
}

/// <summary>Le fonti lette dall'agente come fonti del retrieval: i chunk si ritrovano nel catalogo per id.</summary>
public static class SourceEvidenceExtensions
{
    public static IReadOnlyList<RetrievedSource> ToRetrievedSources(this IReadOnlyList<SourceEvidence> evidence, ChunkCatalog catalog) =>
        [.. evidence
            .Select(source => (source, chunks: Chunks(source, catalog)
                .Select(c => new RetrievedChunk(c.Chunk, null, c.Distance, null, null, source.Score, VersionPenalized: false))
                .ToList()))
            .Where(s => s.chunks.Count > 0)
            .Select((s, index) => new RetrievedSource(index + 1, s.source.Source, s.source.Key, s.source.Score, s.chunks[0], s.chunks))];

    /// <summary>I chunk visti, il migliore (distanza più bassa) per primo; per una fonte intera anche gli altri, senza distanza.</summary>
    private static IEnumerable<(IndexedChunk Chunk, double? Distance)> Chunks(SourceEvidence source, ChunkCatalog catalog)
    {
        List<(IndexedChunk Chunk, double? Distance)> seen = [.. source.Chunks
            .Where(c => catalog.Contains(c.ChunkId))
            .OrderBy(c => c.VectorDistance ?? double.MaxValue)
            .Select(c => (catalog[c.ChunkId], c.VectorDistance))];

        if (!source.IsWhole)
        {
            return seen;
        }

        HashSet<int> known = [.. seen.Select(c => c.Chunk.Id)];

        return seen.Concat(catalog.Chunks
            .Where(c => c.Source == source.Source && string.Equals(c.SourceKey, source.Key, StringComparison.OrdinalIgnoreCase) && !known.Contains(c.Id))
            .OrderBy(c => c.Section)
            .Select(c => (c, (double?)null)));
    }
}
