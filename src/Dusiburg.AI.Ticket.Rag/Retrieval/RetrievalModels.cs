using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Rag.Retrieval;

/// <summary>
/// Un chunk del catalogo in memoria, con la chiave di business della fonte (<c>Number</c> del ticket, <c>Code</c> di articolo e
/// nota: l'id che il modello cita) e l'intervallo di applicabilità come numeri d'ordine delle versioni (<c>Sequence</c>).
/// </summary>
public sealed record IndexedChunk(
    int Id,
    ChunkSource Source,
    string SourceKey,
    short Section,
    short ProductId,
    DeploymentMode? Mode,
    short? VersionFromSequence,
    short? VersionToSequence,
    string Text)
{
    /// <summary>Estremi nulli = aperti; un chunk senza intervallo vale per tutte le versioni.</summary>
    public bool AppliesTo(short versionSequence) =>
        (VersionFromSequence is null || VersionFromSequence <= versionSequence) && (VersionToSequence is null || versionSequence <= VersionToSequence);
}

/// <summary>
/// Contesto del ticket in arrivo: prodotto, modalità e versione del cliente (P4), nulli se non noti. Il testo va già pulito e
/// anonimizzato con <c>SourceText.Query</c>, come i documenti.
/// </summary>
public sealed record RetrievalQuery(string Text, string? ProductCode, DeploymentMode? Mode, string? Version);

/// <summary>Quale classifica usare: le due varianti singole servono al confronto della valutazione.</summary>
public enum RetrievalVariant
{
    Vector,
    Bm25,
    Hybrid
}

public sealed record RetrievalSettings(RetrievalVariant Variant, bool ApplyVersionPenalty)
{
    /// <summary>Quella della pipeline (P5): ibrida con penalità di versione.</summary>
    public static RetrievalSettings Default { get; } = new(RetrievalVariant.Hybrid, ApplyVersionPenalty: true);
}

public sealed record VectorHit(int ChunkId, double Distance);

/// <summary>Un chunk dopo la fusione, con le posizioni nelle due classifiche (da 1, nulle se assente) e il punteggio finale.</summary>
public sealed record RetrievedChunk(
    IndexedChunk Chunk,
    int? VectorRank,
    double? VectorDistance,
    int? Bm25Rank,
    double? Bm25Score,
    double Score,
    bool VersionPenalized);

/// <summary>Una fonte (ticket, articolo, nota) con il suo chunk migliore e i suoi chunk nel top-K, in ordine.</summary>
public sealed record RetrievedSource(int Rank, ChunkSource Source, string Key, double Score, RetrievedChunk Best, IReadOnlyList<RetrievedChunk> Chunks);

/// <summary>Filtri effettivamente applicati: prodotto o versione non riconosciuti valgono come non noti.</summary>
public sealed record AppliedFilters(short? ProductId, DeploymentMode? Mode, short? VersionSequence);

public sealed record RetrievalTimings(TimeSpan Embedding, TimeSpan Vector, TimeSpan Bm25, TimeSpan Total);

/// <param name="PromptSources">Le fonti per il prompt della Fase 6 (<see cref="RankFusion.SelectForPrompt"/>).</param>
public sealed record RetrievalResult(
    IReadOnlyList<RetrievedChunk> Chunks,
    IReadOnlyList<RetrievedSource> Sources,
    IReadOnlyList<RetrievedSource> PromptSources,
    AppliedFilters Filters,
    RetrievalTimings Timings);
