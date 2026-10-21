using Dusiburg.AI.Ticket.Core.Pipeline;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Knowledge;

namespace Dusiburg.AI.Ticket.Agentic;

/// <summary>
/// Ciò che accade in un'elaborazione della modalità agente, registrato dal codice e non dal modello: chiamate ai tool, chiavi delle
/// fonti che il modello può leggere (solo quelle uscite da un risultato di questo run) e fonti lette davvero, con i chunk visti.
/// </summary>
public sealed class ToolAgentRun(int maxToolCalls)
{
    private readonly List<ToolCallRecord> _calls = [];
    private readonly HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EvidenceBuilder> _evidence = [];

    public int MaxToolCalls { get; } = maxToolCalls;

    public IReadOnlyList<ToolCallRecord> Calls => _calls;

    /// <summary>Chiamate arrivate al server (le rifiutate dalla guardia non contano nel limite).</summary>
    public int CallsToServer => _calls.Count(c => !c.Outcome.StartsWith(GuardedTool.RejectedPrefix, StringComparison.Ordinal));

    public IReadOnlyList<SourceEvidence> Evidence => [.. _evidence.Select(e => e.Build())];

    /// <summary>Le chiavi delle fonti lette, nell'ordine: le sole che la bozza può citare.</summary>
    public IReadOnlyList<string> ReadKeys => [.. _evidence.Select(e => e.Key)];

    public void Record(ToolCallRecord call) => _calls.Add(call);

    public void Allow(IEnumerable<string> keys)
    {
        foreach (string key in keys.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            _allowed.Add(key.Trim());
        }
    }

    public bool IsAllowed(string? key) => key is not null && _allowed.Contains(key.Trim());

    /// <summary>Ogni fonte trovata diventa leggibile; il modello ne ha visto l'estratto del chunk migliore.</summary>
    public void AddSearch(KnowledgeSearchResult result)
    {
        foreach (KnowledgeSource source in result.Sources)
        {
            Allow([source.Key]);

            EvidenceBuilder evidence = Find(source.Source, source.Key);
            evidence.Score = Math.Max(evidence.Score, source.Score);

            if (source.Chunks.FirstOrDefault() is { } best)
            {
                evidence.See(best.ChunkId, source.VectorDistance ?? best.VectorDistance);
            }
        }
    }

    /// <summary>Una fonte letta per intero (lettura completa, nota di rilascio).</summary>
    public void AddWhole(ChunkSource source, string key)
    {
        Allow([key]);
        Find(source, key).Whole = true;
    }

    private EvidenceBuilder Find(ChunkSource source, string key)
    {
        EvidenceBuilder? found = _evidence.FirstOrDefault(e => e.Source == source && string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

        if (found is null)
        {
            found = new EvidenceBuilder(source, key);
            _evidence.Add(found);
        }

        return found;
    }

    private sealed class EvidenceBuilder(ChunkSource source, string key)
    {
        private readonly Dictionary<int, double?> _chunks = [];

        public ChunkSource Source { get; } = source;

        public string Key { get; } = key;

        public double Score { get; set; }

        public bool Whole { get; set; }

        public void See(int chunkId, double? distance) =>
            _chunks[chunkId] = _chunks.TryGetValue(chunkId, out double? known) && known is { } k && (distance is null || k < distance) ? k : distance;

        public SourceEvidence Build() => new(Source, Key, Score, [.. _chunks.Select(c => new EvidenceChunk(c.Key, c.Value))], Whole);
    }
}
