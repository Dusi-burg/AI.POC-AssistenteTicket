using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Domain;
using static Dusiburg.AI.Ticket.Rag.Tests.LexicalSearchTests;

namespace Dusiburg.AI.Ticket.Rag.Tests;

/// <summary>Reciprocal Rank Fusion, penalità di versione e raggruppamento per fonte (P5).</summary>
public class RankFusionTests
{
    private static readonly RetrievalOptions Options = RetrievalOptions.Default with { TopK = 10, RrfK = 60, VersionPenalty = 0.5 };

    [Test]
    public void Fuse_ChunkInBothRankings_BeatsChunksInOnlyOne()
    {
        //SETUP
        IndexedChunk a = Chunk(1, "a"), b = Chunk(2, "b"), c = Chunk(3, "c");

        //SUT
        IReadOnlyList<RetrievedChunk> fused = RankFusion.Fuse([(a, 0.1), (b, 0.2)], [(c, 9.0), (b, 5.0)], versionSequence: null, Options, applyVersionPenalty: true);

        Assert.That(fused.Select(f => f.Chunk.Id), Is.EqualTo(new[] { 2, 1, 3 }));
        Assert.That(fused[0].Score, Is.EqualTo(1.0 / 62 + 1.0 / 62).Within(1e-12));
        Assert.That((fused[0].VectorRank, fused[0].Bm25Rank, fused[0].VectorDistance, fused[0].Bm25Score), Is.EqualTo(((int?)2, (int?)2, (double?)0.2, (double?)5.0)));
        Assert.That((fused[1].Bm25Rank, fused[2].VectorRank), Is.EqualTo(((int?)null, (int?)null)));
    }

    [Test]
    public void Fuse_VersionOutOfRange_IsPenalizedOnlyWhenTheVersionIsKnownAndPenaltyApplies()
    {
        //SETUP
        IndexedChunk oldFix = Chunk(1, "fix", source: ChunkSource.ReleaseNote, to: 2);
        IndexedChunk open = Chunk(2, "kb", source: ChunkSource.KbArticle);

        //SUT
        IReadOnlyList<RetrievedChunk> penalized = RankFusion.Fuse([(oldFix, 0.1), (open, 0.2)], [], versionSequence: 4, Options, applyVersionPenalty: true);
        IReadOnlyList<RetrievedChunk> unknownVersion = RankFusion.Fuse([(oldFix, 0.1), (open, 0.2)], [], versionSequence: null, Options, applyVersionPenalty: true);
        IReadOnlyList<RetrievedChunk> disabled = RankFusion.Fuse([(oldFix, 0.1), (open, 0.2)], [], versionSequence: 4, Options, applyVersionPenalty: false);

        Assert.That(penalized.Select(f => f.Chunk.Id), Is.EqualTo(new[] { 2, 1 }));
        Assert.That(penalized[1].VersionPenalized, Is.True);
        Assert.That(penalized[1].Score, Is.EqualTo(0.5 / 61).Within(1e-12));
        Assert.That(unknownVersion.Select(f => f.Chunk.Id), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(disabled.Select(f => f.Chunk.Id), Is.EqualTo(new[] { 1, 2 }));
    }

    [TestCase(null, null, 3, true)]
    [TestCase((short)2, null, 1, false)]
    [TestCase((short)2, null, 2, true)]
    [TestCase(null, (short)3, 4, false)]
    [TestCase((short)2, (short)3, 3, true)]
    public void AppliesTo_OpenBoundsAndInclusiveLimits(short? from, short? to, short version, bool expected)
    {
        //SUT
        Assert.That(Chunk(1, "x", from: from, to: to).AppliesTo(version), Is.EqualTo(expected));
    }

    [Test]
    public void Fuse_KeepsOnlyTopK()
    {
        //SETUP
        List<(IndexedChunk, double)> vector = [.. Enumerable.Range(1, 15).Select(i => (Chunk(i, "x"), i / 100.0))];

        //SUT
        IReadOnlyList<RetrievedChunk> fused = RankFusion.Fuse(vector, [], null, Options with { TopK = 5 }, applyVersionPenalty: false);

        Assert.That(fused.Select(f => f.Chunk.Id), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public void SelectForPrompt_OnlyTickets_LastPlaceGoesToTheBestDocument()
    {
        //SETUP
        IReadOnlyList<RetrievedSource> onlyTicketsFirst = Sources(
            ("HT-1", ChunkSource.HistoricalTicket), ("HT-2", ChunkSource.HistoricalTicket), ("HT-3", ChunkSource.HistoricalTicket),
            ("HT-4", ChunkSource.HistoricalTicket), ("HT-5", ChunkSource.HistoricalTicket), ("RN-1", ChunkSource.ReleaseNote), ("KB-1", ChunkSource.KbArticle));
        IReadOnlyList<RetrievedSource> documentInside = Sources(
            ("HT-1", ChunkSource.HistoricalTicket), ("KB-1", ChunkSource.KbArticle), ("HT-2", ChunkSource.HistoricalTicket),
            ("HT-3", ChunkSource.HistoricalTicket), ("RN-1", ChunkSource.ReleaseNote));
        IReadOnlyList<RetrievedSource> noDocument = Sources(("HT-1", ChunkSource.HistoricalTicket), ("HT-2", ChunkSource.HistoricalTicket),
            ("HT-3", ChunkSource.HistoricalTicket), ("HT-4", ChunkSource.HistoricalTicket), ("HT-5", ChunkSource.HistoricalTicket));

        //SUT
        Assert.That(RankFusion.SelectForPrompt(onlyTicketsFirst, 4).Select(s => s.Key), Is.EqualTo(new[] { "HT-1", "HT-2", "HT-3", "RN-1" }));
        Assert.That(RankFusion.SelectForPrompt(documentInside, 4).Select(s => s.Key), Is.EqualTo(new[] { "HT-1", "KB-1", "HT-2", "HT-3" }));
        Assert.That(RankFusion.SelectForPrompt(noDocument, 4).Select(s => s.Key), Is.EqualTo(new[] { "HT-1", "HT-2", "HT-3", "HT-4" }));
        Assert.That(RankFusion.SelectForPrompt(Sources(("HT-1", ChunkSource.HistoricalTicket)), 4).Select(s => s.Key), Is.EqualTo(new[] { "HT-1" }));
    }

    [Test]
    public void GroupBySource_OneRowPerSourceWithItsBestChunk()
    {
        //SETUP
        IndexedChunk kbSection1 = Chunk(1, "s1", source: ChunkSource.KbArticle, key: "KB-FF-0004");
        IndexedChunk ticket = Chunk(2, "t", key: "HT-2026-0001");
        IndexedChunk kbSection2 = Chunk(3, "s2", source: ChunkSource.KbArticle, key: "KB-FF-0004");
        IReadOnlyList<RetrievedChunk> fused = RankFusion.Fuse([(kbSection2, 0.1), (ticket, 0.2), (kbSection1, 0.3)], [], null, Options, false);

        //SUT
        IReadOnlyList<RetrievedSource> sources = RankFusion.GroupBySource(fused);

        Assert.That(sources.Select(s => (s.Rank, s.Key)), Is.EqualTo(new[] { (1, "KB-FF-0004"), (2, "HT-2026-0001") }));
        Assert.That(sources[0].Best.Chunk.Id, Is.EqualTo(3));
        Assert.That(sources[0].Chunks.Select(c => c.Chunk.Id), Is.EqualTo(new[] { 3, 1 }));
        Assert.That(sources[0].Score, Is.EqualTo(fused[0].Score));
    }

    /// <summary>Fonti in quest'ordine, una per chiave, dalla sola classifica vettoriale.</summary>
    private static IReadOnlyList<RetrievedSource> Sources(params (string Key, ChunkSource Source)[] keys) =>
        RankFusion.GroupBySource(RankFusion.Fuse(
            [.. keys.Select((k, i) => (Chunk(i + 1, "x", source: k.Source, key: k.Key), i / 100.0))], [], null, Options, applyVersionPenalty: false));
}
