namespace Dusiburg.AI.Ticket.Ai.Tests;

/// <summary>Prefissi e dimensione nativa per modello di embedding (G6, profilo di Sinistri).</summary>
public class EmbeddingProfileTests
{
    [TestCase("embeddinggemma")]
    [TestCase("embeddinggemma:latest")]
    [TestCase(" EmbeddingGemma ")]
    public void ForModel_EmbeddingGemma_QueryAndDocumentPrefixes(string model)
    {
        //SUT
        EmbeddingProfile profile = EmbeddingProfile.ForModel(model);

        Assert.That(profile.NativeDimensions, Is.EqualTo(768));
        Assert.That(profile.Query("firma scaduta"), Is.EqualTo("task: search result | query: firma scaduta"));
        Assert.That(profile.Document("firma scaduta"), Is.EqualTo("title: none | text: firma scaduta"));
    }

    [TestCase("qwen3-embedding:4b", 2560)]
    [TestCase("qwen3-embedding:8b", 4096)]
    [TestCase("qwen3-embedding", 1024)]
    [TestCase("bge-m3:latest", 1024)]
    public void ForModel_KnownModel_NativeDimensionsByTag(string model, int expected)
    {
        //SUT
        Assert.That(EmbeddingProfile.ForModel(model).NativeDimensions, Is.EqualTo(expected));
    }

    [Test]
    public void ForModel_UnknownModel_ThrowsListingTheSupportedOnes()
    {
        //SUT
        Assert.That(() => EmbeddingProfile.ForModel("nomic-embed-text"),
            Throws.InvalidOperationException.With.Message.Contains("nomic-embed-text").And.Message.Contains("embeddinggemma"));
    }
}
