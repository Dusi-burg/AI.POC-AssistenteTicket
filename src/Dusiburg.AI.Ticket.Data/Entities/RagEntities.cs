using Dusiburg.AI.Ticket.Shared.Domain;
using Microsoft.Data.SqlTypes;

namespace Dusiburg.AI.Ticket.Data.Entities;

// Schema rag: i frammenti indicizzati con i metadati di applicabilità e il vettore. EF crea le tabelle; le query vettoriali
// sono Dapper con SqlVector<float> (G4, Fase 5).

/// <summary>
/// Frammento di una fonte (ticket risolto, sezione di un articolo, nota di rilascio). Solo una delle tre FK verso la fonte è
/// valorizzata, quella di <see cref="Source"/>. Prodotto, modalità e intervallo di versioni sono copiati dalla fonte, così il
/// retrieval filtra senza join.
/// </summary>
public sealed class Chunk
{
    public int Id { get; set; }

    public ChunkSource Source { get; set; }

    public int? HistoricalTicketId { get; set; }

    public int? KbArticleId { get; set; }

    public int? ReleaseNoteId { get; set; }

    /// <summary>Posizione del frammento nella fonte, da 1.</summary>
    public short Section { get; set; }

    public short ProductId { get; set; }

    public DeploymentMode? DeploymentMode { get; set; }

    public int? VersionFromId { get; set; }

    public int? VersionToId { get; set; }

    public required string Text { get; set; }

    /// <summary><c>null</c> finché <c>Cli embed</c> non lo calcola. La dimensione è quella del modello (<c>EMBEDDING_DIMENSIONS</c>).</summary>
    public SqlVector<float>? Embedding { get; set; }
}

/// <summary>
/// Riga unica (<see cref="Id"/> = 1): modello e dimensione con cui sono stati calcolati i vettori salvati. Impedisce di mescolare
/// vettori di modelli diversi (come Sinistri); vuota finché non si calcolano gli embedding.
/// </summary>
public sealed class EmbeddingInfo
{
    public const byte SingletonId = 1;

    public byte Id { get; set; } = SingletonId;

    public required string Model { get; set; }

    public short Dimensions { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
