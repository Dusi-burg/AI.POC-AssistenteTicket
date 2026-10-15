using Microsoft.Extensions.Configuration;

namespace Dusiburg.AI.Ticket.Ai.Tests;

/// <summary>Manopole dei modelli: default del PLAN §8 e validazione dei valori.</summary>
public class AiOptionsTests
{
    [Test]
    public void FromConfiguration_Empty_UsesPlanDefaults()
    {
        //SUT
        AiOptions options = Options([]);

        Assert.That(options.OllamaEndpoint, Is.EqualTo(new Uri("http://127.0.0.1:11434")));
        Assert.That(options.OllamaChatModel, Is.EqualTo("qwen3.5:9b"));
        Assert.That(options.OllamaContextLength, Is.EqualTo(8_192));
        Assert.That(options.OllamaNumGpu, Is.EqualTo(99));
        Assert.That(options.AnthropicModel, Is.EqualTo("claude-haiku-5-5"));
        Assert.That(options.HasAnthropicApiKey, Is.False);
        Assert.That(options.EmbeddingModel, Is.EqualTo("embeddinggemma"));
        Assert.That(options.EmbeddingDimensions, Is.EqualTo(768));
        Assert.That(options.PromptCaptureDirectory, Is.Null);
    }

    [TestCase(ModelTask.Classify)]
    [TestCase(ModelTask.Draft)]
    public void ProviderFor_Default_EverythingLocal(ModelTask task)
    {
        //SETUP
        AiOptions options = Options([]);

        //SUT
        Assert.That(options.ProviderFor(task), Is.EqualTo(ModelProviders.Ollama));
    }

    [Test]
    public void ProviderFor_ConfiguredTask_ChangesOnlyThatTask()
    {
        //SETUP
        AiOptions options = Options(new() { ["MODEL_DRAFT_PROVIDER"] = " Anthropic " });

        //SUT
        Assert.That(options.ProviderFor(ModelTask.Draft), Is.EqualTo(ModelProviders.Anthropic));
        Assert.That(options.ProviderFor(ModelTask.Classify), Is.EqualTo(ModelProviders.Ollama));
    }

    [Test]
    public void FromConfiguration_NumGpuAuto_LeavesChoiceToOllama()
    {
        //SUT
        AiOptions options = Options(new() { ["OLLAMA_NUM_GPU"] = "AUTO" });

        Assert.That(options.OllamaNumGpu, Is.Null);
    }

    [Test]
    public void KeysAll_ContainsEveryTaskProvider()
    {
        //SUT
        IReadOnlyList<string> keys = AiOptions.Keys.All;

        Assert.That(keys, Does.Contain("MODEL_CLASSIFY_PROVIDER").And.Contain("MODEL_DRAFT_PROVIDER").And.Contain("OLLAMA_NUM_GPU"));
        Assert.That(keys, Does.Not.Contain("ANTHROPIC_API_KEY"), "la chiave è un parametro segreto, non una manopola");
    }

    [TestCase("MODEL_CLASSIFY_PROVIDER", "openai")]
    [TestCase("OLLAMA_ENDPOINT", "localhost:11434")]
    [TestCase("OLLAMA_NUM_CTX", "512")]
    [TestCase("OLLAMA_NUM_GPU", "tutti")]
    [TestCase("EMBEDDING_DIMENSIONS", "4000")]
    [TestCase("EMBEDDING_DIMENSIONS", "settecento")]
    public void FromConfiguration_InvalidValue_ThrowsNamingTheKey(string key, string value)
    {
        //SUT
        Assert.That(() => Options(new() { [key] = value }), Throws.InvalidOperationException.With.Message.Contains(key));
    }

    private static AiOptions Options(Dictionary<string, string?> settings) =>
        AiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
}
