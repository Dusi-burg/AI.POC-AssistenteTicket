using Dusiburg.AI.Ticket.Ai.Health;
using Dusiburg.AI.Ticket.Shared.Health;
using Microsoft.Extensions.Configuration;

namespace Dusiburg.AI.Ticket.Ai.Tests;

/// <summary>Controllo 9: la chiave serve solo se un compito è instradato sul cloud.</summary>
public class AnthropicKeyHealthProbeTests
{
    [Test]
    public async Task RunAsync_NoKeyAndEverythingLocal_InfoNotRequired()
    {
        //SETUP
        var probe = new AnthropicKeyHealthProbe(Options([]));

        //SUT
        ProbeResult result = (await probe.RunAsync(CancellationToken.None)).Single();

        Assert.That((result.Number, result.Status), Is.EqualTo((9, ProbeStatus.Info)));
        Assert.That(result.Detail, Does.Contain("non richiesta"));
    }

    [Test]
    public async Task RunAsync_NoKeyButDraftOnCloud_ErrorNamingTheParameter()
    {
        //SETUP
        var probe = new AnthropicKeyHealthProbe(Options(new() { ["MODEL_DRAFT_PROVIDER"] = "anthropic" }));

        //SUT
        ProbeResult result = (await probe.RunAsync(CancellationToken.None)).Single();

        Assert.That(result.Status, Is.EqualTo(ProbeStatus.Error));
        Assert.That(result.Detail, Does.Contain("Parameters:anthropic-api-key").And.Contain("Draft"));
    }

    [Test]
    public async Task RunAsync_KeyConfigured_OkWithoutCallingTheApi()
    {
        //SETUP
        var probe = new AnthropicKeyHealthProbe(Options(new() { ["ANTHROPIC_API_KEY"] = "test-key" }));

        //SUT
        ProbeResult result = (await probe.RunAsync(CancellationToken.None)).Single();

        Assert.That(result.Status, Is.EqualTo(ProbeStatus.Ok));
        Assert.That(result.Detail, Does.Not.Contain("test-key"));
    }

    private static AiOptions Options(Dictionary<string, string?> settings) =>
        AiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
}
