using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Rag.Tests;

/// <summary>Tokenizzazione italiana e BM25 in memoria (P5).</summary>
public class LexicalSearchTests
{
    [Test]
    public void Tokenize_ErrorCodesStayWholeAndAlsoInParts()
    {
        //SUT
        IReadOnlyList<string> tokens = ItalianTokenizer.Tokenize("Errore FF-E2041 sulla 5.1.2");

        Assert.That(tokens, Is.EqualTo(new[] { "error", "ff-e2041", "ff", "e2041", "5.1.2" }));
    }

    [Test]
    public void Tokenize_InflectedFormsAndAccents_Coincide()
    {
        //SUT
        Assert.That(ItalianTokenizer.Tokenize("fattura bloccata"), Is.EqualTo(ItalianTokenizer.Tokenize("fatture bloccate")));
        Assert.That(ItalianTokenizer.Tokenize("configurazione"), Is.EqualTo(ItalianTokenizer.Tokenize("configurato")));
        Assert.That(ItalianTokenizer.Tokenize("funzionalità"), Is.EqualTo(ItalianTokenizer.Tokenize("funzionalita")));
    }

    [Test]
    public void Tokenize_StopWordsAndApostrophes_AreDropped()
    {
        //SUT
        IReadOnlyList<string> tokens = ItalianTokenizer.Tokenize("Buongiorno, l'invio della fattura non è partito");

        Assert.That(tokens, Is.EqualTo(new[] { "invi", "fattur", "part" }));
    }

    [Test]
    public void Search_RareTermWeighsMoreThanACommonOne()
    {
        //SETUP
        var index = new Bm25Index(
        [
            Chunk(1, "fattura scartata dal sistema di interscambio"),
            Chunk(2, "fattura scartata con errore FF-E2210 file duplicato"),
            Chunk(3, "fattura emessa correttamente"),
        ]);

        //SUT
        IReadOnlyList<(IndexedChunk Chunk, double Score)> hits = index.Search("fattura FF-E2210", _ => true, top: 3);

        Assert.That(hits, Has.Count.EqualTo(3));
        Assert.That(hits[0].Chunk.Id, Is.EqualTo(2));
        Assert.That(hits[0].Score, Is.GreaterThan(hits[1].Score * 2));
    }

    [Test]
    public void Search_FilterAndTop_AreApplied()
    {
        //SETUP
        var index = new Bm25Index([Chunk(1, "ricerca lenta", productId: 1), Chunk(2, "ricerca lenta indice", productId: 2), Chunk(3, "ricerca", productId: 2)]);

        //SUT
        IReadOnlyList<(IndexedChunk Chunk, double Score)> hits = index.Search("ricerca lenta", c => c.ProductId == 2, top: 1);
        IReadOnlyList<(IndexedChunk Chunk, double Score)> none = index.Search("parola assente", _ => true, top: 5);

        Assert.That(hits.Select(h => h.Chunk.Id), Is.EqualTo(new[] { 2 }));
        Assert.That(none, Is.Empty);
    }

    internal static IndexedChunk Chunk(int id, string text, short productId = 1, ChunkSource source = ChunkSource.HistoricalTicket, string? key = null,
        DeploymentMode? mode = null, short? from = null, short? to = null) =>
        new(id, source, key ?? $"K{id}", 1, productId, mode, from, to, text);
}
