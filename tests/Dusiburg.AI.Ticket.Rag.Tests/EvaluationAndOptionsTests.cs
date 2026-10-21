using Dusiburg.AI.Ticket.Rag.Evaluation;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Domain;
using Microsoft.Extensions.Configuration;
using static Dusiburg.AI.Ticket.Rag.Tests.LexicalSearchTests;

namespace Dusiburg.AI.Ticket.Rag.Tests;

/// <summary>Metriche della valutazione e manopole del retrieval.</summary>
public class EvaluationAndOptionsTests
{
    [Test]
    public void Metrics_FirstRelevantRankHitAndReciprocalRank()
    {
        //SETUP
        HashSet<string> relevant = ["B", "D"];
        string[] ranked = ["A", "B", "C", "D"];

        //SUT
        Assert.That(EvalMetrics.FirstRelevantRank(relevant, ranked), Is.EqualTo(2));
        Assert.That(EvalMetrics.FirstRelevantRank(relevant, ranked, maxRank: 1), Is.Null);
        Assert.That(EvalMetrics.HitAtK(relevant, ranked, 1), Is.False);
        Assert.That(EvalMetrics.HitAtK(relevant, ranked, 2), Is.True);
        Assert.That(EvalMetrics.ReciprocalRank(relevant, ranked), Is.EqualTo(0.5));
        Assert.That(EvalMetrics.ReciprocalRank(["Z"], ranked), Is.Zero);
        Assert.That(EvalMetrics.Percentile([5, 1, 3, 2, 4], 95), Is.EqualTo(5));
        Assert.That(EvalMetrics.Mean([]), Is.Zero);
    }

    [Test]
    public void Score_PromptPrecisionAndDocumentOnTheFirstSources()
    {
        //SETUP
        var evalCase = new EvalCase("HT-T", "CASE", null, "FF", DeploymentMode.OnPrem, "5.1.2", "q", null,
            new HashSet<string> { "HT-1", "KB-1" }, new HashSet<string> { "KB-1" });
        IReadOnlyList<RetrievedChunk> fused = RankFusion.Fuse(
            [(Chunk(1, "x", key: "HT-9"), 0.1), (Chunk(2, "x", key: "HT-1"), 0.2), (Chunk(3, "x", key: "HT-8"), 0.3), (Chunk(4, "x", key: "HT-7"), 0.4),
             (Chunk(5, "x", source: ChunkSource.KbArticle, key: "KB-1"), 0.5)],
            [], null, RetrievalOptions.Default, false);
        IReadOnlyList<RetrievedSource> sources = RankFusion.GroupBySource(fused);
        var result = new RetrievalResult(fused, sources, [.. sources.Take(4)], new AppliedFilters(1, null, null),
            new RetrievalTimings(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));

        //SUT
        EvalCaseResult scored = RetrievalEvaluator.Score(evalCase, result);

        Assert.That(scored.FirstRelevantRank, Is.EqualTo(2));
        Assert.That(scored.PromptPrecision, Is.EqualTo(0.25));
        Assert.That(scored.PromptHasDocument, Is.False);
        Assert.That(scored.HitAt(5), Is.True);
    }

    [Test]
    public void Options_Defaults_AndValuesFromConfiguration()
    {
        //SETUP
        IConfiguration empty = new ConfigurationBuilder().Build();
        IConfiguration custom = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RetrievalOptions.Keys.TopK] = "30",
            [RetrievalOptions.Keys.PromptSourcesMax] = "3",
            [RetrievalOptions.Keys.RrfK] = "20",
            [RetrievalOptions.Keys.VersionPenalty] = "0.8"
        }).Build();

        //SUT
        Assert.That(RetrievalOptions.FromConfiguration(empty), Is.EqualTo(RetrievalOptions.Default));
        Assert.That(RetrievalOptions.FromConfiguration(custom), Is.EqualTo(new RetrievalOptions(30, 3, 20, 0.8)));
    }

    [TestCase(RetrievalOptions.Keys.TopK, "0")]
    [TestCase(RetrievalOptions.Keys.RrfK, "dieci")]
    [TestCase(RetrievalOptions.Keys.VersionPenalty, "0")]
    [TestCase(RetrievalOptions.Keys.VersionPenalty, "1.5")]
    [TestCase(RetrievalOptions.Keys.VersionPenalty, "0,5")]
    public void Options_InvalidValue_ThrowsNamingTheKnob(string key, string value)
    {
        //SETUP
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

        //SUT
        Assert.That(() => RetrievalOptions.FromConfiguration(configuration), Throws.InvalidOperationException.With.Message.Contains(key));
    }
}
