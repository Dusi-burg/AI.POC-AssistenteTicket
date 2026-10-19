using Dusiburg.AI.Ticket.Core.Context;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Core.Tests.Support;

/// <summary>Ticket, fonti e uscite del modello di prova, costruiti in modo esplicito.</summary>
internal static class TestData
{
    public static readonly DateTimeOffset SentAt = new(2026, 10, 16, 9, 30, 0, TimeSpan.FromHours(2));

    public static CustomerContext Customer(DeploymentMode? mode = DeploymentMode.OnPrem, string? version = "5.1.2") =>
        new("Studio Contabile Ferretti S.r.l.", "Giorgio Ferretti", "giorgio.ferretti@ferretti.example.it", "FF", version, mode, SlaLevel.Plus, "organizzazione Zammad");

    public static IncomingTicket Ticket(string text = "Da stamattina le fatture vanno in errore FF-E2041.", CustomerContext? customer = null) =>
        new(170, "71170", "Fatture non trasmesse: errore FF-E2041", 429,
            [new ThreadMessage(429, MessageAuthor.Customer, SentAt, text)], customer ?? Customer());

    public static RetrievedSource Source(int rank, string key, ChunkSource source, string text, double? distance) =>
        new(rank, source, key, 0.03,
            new RetrievedChunk(new IndexedChunk(rank, source, key, 1, 1, null, null, null, text), distance is null ? null : rank, distance, 1, 5, 0.03, false),
            [new RetrievedChunk(new IndexedChunk(rank, source, key, 1, 1, null, null, null, text), distance is null ? null : rank, distance, 1, 5, 0.03, false)]);

    public static IReadOnlyList<RetrievedSource> Sources() =>
    [
        Source(1, "KB-FF-0004", ChunkSource.KbArticle,
            "Errore FF-E2041 — Soluzione\nAggiornare la catena in Impostazioni > Trasmissione e riavviare il servizio FattureFlow Sender. Comando: ffadmin truststore import.", 0.15),
        Source(2, "HT-2026-0280", ChunkSource.HistoricalTicket, "Fatture in errore FF-E2041 dalla 5.1.2. Soluzione: importata la nuova catena.", 0.14)
    ];

    public static ModelClassification Classification(int missing = 0, bool manipulation = false) =>
        new("trasmissione", "alta", "it", [.. Enumerable.Range(1, missing).Select(i => $"informazione {i}")], manipulation);

    public static ModelDraft Draft(string answer = "Gentile Giorgio,\n\nle chiediamo di aprire Impostazioni > Trasmissione.\n\nCordiali saluti,", params string[] cited) =>
        new(true, 0.9, answer, [.. cited.Select(id => new ModelSource(id, $"motivo {id}"))], []);
}
