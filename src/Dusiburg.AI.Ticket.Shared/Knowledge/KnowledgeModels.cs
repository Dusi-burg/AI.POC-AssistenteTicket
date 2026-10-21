using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Shared.Knowledge;

// API di conoscenza dell'Agent (/api/knowledge, Fase 9b), in sola lettura: le usa il server MCP, che non ha un database. Testi
// dall'indice (puliti e anonimizzati come in Fase 5) o dai documenti del prodotto (articoli, note di rilascio).

/// <summary>Un chunk di una fonte trovata, con la distanza coseno se è uscito dalla componente vettoriale.</summary>
public sealed record KnowledgeChunkRef(int ChunkId, double? VectorDistance);

/// <param name="Applicability">Prodotto, modalità e intervallo di versioni a cui vale la fonte, in chiaro (<c>FF · on-premise · 5.0.3–5.2.0</c>).</param>
/// <param name="Excerpt">Il testo del chunk migliore, tagliato.</param>
public sealed record KnowledgeSource(
    int Rank,
    ChunkSource Source,
    string Key,
    double Score,
    double? VectorDistance,
    bool VersionPenalized,
    string Applicability,
    string Excerpt,
    IReadOnlyList<KnowledgeChunkRef> Chunks);

/// <summary>Esito di una ricerca: le fonti e i filtri effettivamente applicati.</summary>
public sealed record KnowledgeSearchResult(string? ProductCode, DeploymentMode? Mode, string? Version, IReadOnlyList<KnowledgeSource> Sources);

public sealed record KnowledgeChunk(int ChunkId, short Section, string Text);

/// <summary>Una fonte per intero: tutti i suoi chunk dell'indice.</summary>
public sealed record KnowledgeDocument(ChunkSource Source, string Key, string Applicability, IReadOnlyList<KnowledgeChunk> Chunks);

public sealed record ReleaseNoteItem(string Code, string ProductCode, string Version, ReleaseNoteType Type, string Title, string Body);

/// <summary>Un ticket dello storico del cliente; <see cref="Indexed"/> se si può leggere per intero come fonte.</summary>
public sealed record CustomerTicketItem(string Number, string Subject, string Category, DateTimeOffset OpenedAt, TicketState State, bool Indexed);

public sealed record CustomerHistory(string? CustomerName, IReadOnlyList<CustomerTicketItem> Tickets);
