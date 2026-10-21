using Dusiburg.AI.Ticket.Data;
using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Rag.Cleaning;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.ServiceDefaults.Problems;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Knowledge;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dusiburg.AI.Ticket.Agent.Knowledge;

/// <summary>
/// <c>/api/knowledge</c> (Fase 9b): ricerca, lettura delle fonti, note di rilascio e storico di un cliente, in sola lettura, per il
/// server MCP (<c>ticket-mcp</c>). L'Agent resta l'unico proprietario del database (PLAN §4): il server MCP passa da qui. Il testo
/// che esce è quello dell'indice, pulito e anonimizzato; la ricerca anonimizza anche il testo della domanda.
/// </summary>
public static class KnowledgeEndpoints
{
    public const int DefaultMaxSources = 8;
    public const int MaxSources = 20;
    public const int ExcerptMaxChars = 700;
    public const int DefaultMaxTickets = 10;

    public static RouteGroupBuilder MapKnowledgeEndpoints(this RouteGroupBuilder group)
    {
        RouteGroupBuilder knowledge = group.MapGroup("/knowledge");

        knowledge.MapGet("/search", SearchAsync);
        knowledge.MapGet("/sources/{key}", GetSourceAsync);
        knowledge.MapGet("/release-notes", ReleaseNotesAsync);
        knowledge.MapGet("/customer-tickets", CustomerTicketsAsync);

        return group;
    }

    /// <summary>Retrieval ibrido della pipeline (P5) su un testo scelto da chi chiama, con i filtri del cliente.</summary>
    internal static async Task<Results<Ok<KnowledgeSearchResult>, ProblemHttpResult>> SearchAsync(
        [FromQuery] string? text, [FromQuery] string? product, [FromQuery] DeploymentMode? mode, [FromQuery] string? version, [FromQuery] int? max,
        HybridRetriever retriever, TicketDbContext db, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ApiProblems.Validation("Testo della ricerca assente.");
        }

        int count = Math.Clamp(max ?? DefaultMaxSources, 1, MaxSources);
        RetrievalResult result = await retriever.SearchAsync(
            new RetrievalQuery(Anonymizer.Anonymize(text.Trim(), KnownNames.None), product, mode, version), cancellationToken);
        Applicability applicability = await Applicability.LoadAsync(db, cancellationToken);

        return TypedResults.Ok(new KnowledgeSearchResult(product, mode, version, [.. result.Sources.Take(count).Select(source => new KnowledgeSource(
            source.Rank, source.Source, source.Key, Math.Round(source.Score, 4), Round(source.Chunks.Select(c => c.VectorDistance).Min()),
            source.Chunks.Any(c => c.VersionPenalized), applicability.Of(source.Best.Chunk), Excerpt(source.Best.Chunk.Text),
            [.. source.Chunks.Select(c => new KnowledgeChunkRef(c.Chunk.Id, Round(c.VectorDistance)))]))]));
    }

    /// <summary>Tutti i chunk indicizzati di una fonte, per chiave di business (numero del ticket, codice di articolo o nota).</summary>
    internal static async Task<Results<Ok<KnowledgeDocument>, ProblemHttpResult>> GetSourceAsync(
        string key, HybridRetriever retriever, TicketDbContext db, CancellationToken cancellationToken)
    {
        ChunkCatalog catalog = await retriever.CatalogAsync(cancellationToken);
        List<IndexedChunk> chunks = [.. catalog.Chunks.Where(c => string.Equals(c.SourceKey, key.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Section)];

        if (chunks.Count == 0)
        {
            return ApiProblems.NotFound($"Fonte '{key}' non presente nell'indice.");
        }

        Applicability applicability = await Applicability.LoadAsync(db, cancellationToken);

        return TypedResults.Ok(new KnowledgeDocument(chunks[0].Source, chunks[0].SourceKey, applicability.Of(chunks[0]),
            [.. chunks.Select(c => new KnowledgeChunk(c.Id, c.Section, c.Text))]));
    }

    /// <summary>Note di rilascio di un prodotto, eventualmente fra due versioni (estremi compresi), in ordine di versione.</summary>
    internal static async Task<Results<Ok<List<ReleaseNoteItem>>, ProblemHttpResult>> ReleaseNotesAsync(
        [FromQuery] string? product, [FromQuery] string? from, [FromQuery] string? to, TicketDbContext db, CancellationToken cancellationToken)
    {
        Product? found = product is null ? null : await db.Products.AsNoTracking().Include(p => p.Versions)
            .SingleOrDefaultAsync(p => p.Code == product.Trim(), cancellationToken);

        if (found is null)
        {
            return ApiProblems.Validation($"Prodotto '{product}' sconosciuto.");
        }

        short? fromSequence = found.Versions.SingleOrDefault(v => v.Version == from?.Trim())?.Sequence;
        short? toSequence = found.Versions.SingleOrDefault(v => v.Version == to?.Trim())?.Sequence;

        if (from is { Length: > 0 } && fromSequence is null || to is { Length: > 0 } && toSequence is null)
        {
            return ApiProblems.Validation($"Versione sconosciuta per {found.Code}: usare una fra {string.Join(", ", found.Versions.OrderBy(v => v.Sequence).Select(v => v.Version))}.");
        }

        Dictionary<int, ProductVersion> versions = found.Versions
            .Where(v => (fromSequence is null || v.Sequence >= fromSequence) && (toSequence is null || v.Sequence <= toSequence))
            .ToDictionary(v => v.Id);
        List<ReleaseNote> notes = await db.ReleaseNotes.AsNoTracking().Where(n => versions.Keys.Contains(n.ProductVersionId)).ToListAsync(cancellationToken);

        return TypedResults.Ok(notes
            .OrderBy(n => versions[n.ProductVersionId].Sequence).ThenBy(n => n.Code, StringComparer.Ordinal)
            .Select(n => new ReleaseNoteItem(n.Code, found.Code, versions[n.ProductVersionId].Version, n.Type, n.Title, n.Body))
            .ToList());
    }

    /// <summary>
    /// I ticket più recenti dell'azienda del referente (dominio dell'email), esclusi quelli del test set: oggetto e stato, e se la
    /// soluzione è nell'indice. Il contenuto si legge come fonte, già anonimizzato.
    /// </summary>
    internal static async Task<Results<Ok<CustomerHistory>, ProblemHttpResult>> CustomerTicketsAsync(
        [FromQuery] string? email, [FromQuery] int? max, HybridRetriever retriever, TicketDbContext db, CancellationToken cancellationToken)
    {
        string? domain = email?.Split('@') is [_, var d] && d.Length > 0 ? d.Trim().ToLowerInvariant() : null;

        if (domain is null)
        {
            return ApiProblems.Validation($"Email '{email}' non valida.");
        }

        Customer? customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.EmailDomain == domain, cancellationToken);

        if (customer is null)
        {
            return TypedResults.Ok(new CustomerHistory(null, []));
        }

        ChunkCatalog catalog = await retriever.CatalogAsync(cancellationToken);
        HashSet<string> indexed = [.. catalog.Chunks.Where(c => c.Source == ChunkSource.HistoricalTicket).Select(c => c.SourceKey)];
        List<HistoricalTicket> tickets = await db.HistoricalTickets.AsNoTracking()
            .Where(t => t.CustomerId == customer.Id && !t.IsTestSet)
            .OrderByDescending(t => t.OpenedAt)
            .Take(Math.Clamp(max ?? DefaultMaxTickets, 1, MaxSources))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new CustomerHistory(customer.Name,
            [.. tickets.Select(t => new CustomerTicketItem(t.Number, t.Subject, t.Category, t.OpenedAt, t.State, indexed.Contains(t.Number)))]));
    }

    private static string Excerpt(string text) => text.Length <= ExcerptMaxChars ? text : $"{text[..ExcerptMaxChars].TrimEnd()}…";

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 4) : null;

    /// <summary>Codici dei prodotti e versioni per numero d'ordine: l'applicabilità dei chunk in chiaro.</summary>
    private sealed class Applicability(Dictionary<short, string> products, Dictionary<(short, short), string> versions)
    {
        public static async Task<Applicability> LoadAsync(TicketDbContext db, CancellationToken cancellationToken)
        {
            List<Product> all = await db.Products.AsNoTracking().Include(p => p.Versions).ToListAsync(cancellationToken);

            return new Applicability(
                all.ToDictionary(p => p.Id, p => p.Code),
                all.SelectMany(p => p.Versions).ToDictionary(v => (v.ProductId, v.Sequence), v => v.Version));
        }

        public string Of(IndexedChunk chunk)
        {
            string product = products.GetValueOrDefault(chunk.ProductId, "?");
            string mode = chunk.Mode switch { DeploymentMode.OnPrem => "on-premise", DeploymentMode.Saas => "SaaS", _ => "on-premise e SaaS" };
            string? from = chunk.VersionFromSequence is { } f ? versions.GetValueOrDefault((chunk.ProductId, f)) : null;
            string? to = chunk.VersionToSequence is { } t ? versions.GetValueOrDefault((chunk.ProductId, t)) : null;
            string range = (from, to) switch
            {
                (null, null) => "tutte le versioni",
                (_, null) => $"dalla {from}",
                (null, _) => $"fino alla {to}",
                _ when from == to => $"solo la {from}",
                _ => $"{from}–{to}"
            };

            return $"{product} · {mode} · {range}";
        }
    }
}
