using Dusiburg.AI.Ticket.Shared.Health;

namespace Dusiburg.AI.Ticket.Ai.Health;

/// <summary>
/// Controllo 9 di <c>/api/health</c>: la chiave di Anthropic serve solo se un compito è instradato sul cloud (confronto della
/// Fase 8, G10). Si guarda solo la presenza, senza chiamare l'API: un controllo ripetuto dal dashboard non deve costare.
/// </summary>
public sealed class AnthropicKeyHealthProbe(AiOptions options) : IHealthProbe
{
    public const int Number = 9;

    public const string Name = "Chiave Anthropic";

    public Task<IReadOnlyList<ProbeResult>> RunAsync(CancellationToken cancellationToken)
    {
        ModelTask[] routed = [.. Enum.GetValues<ModelTask>().Where(task => options.ProviderFor(task) == ModelProviders.Anthropic)];
        string tasks = routed.Length == 0 ? "nessun compito sul cloud" : $"compiti sul cloud: {string.Join(", ", routed)}";

        ProbeResult result = (options.HasAnthropicApiKey, routed.Length > 0) switch
        {
            (true, _) => new(Number, Name, ProbeStatus.Ok, $"configurata (non verificata con una chiamata); {tasks}", TimeSpan.Zero),
            (false, false) => new(Number, Name, ProbeStatus.Info, $"non configurata e non richiesta: {tasks}", TimeSpan.Zero),
            (false, true) => new(Number, Name, ProbeStatus.Error,
                $"non configurata ma {tasks}: impostare Parameters:anthropic-api-key negli user-secrets dell'AppHost", TimeSpan.Zero)
        };

        return Task.FromResult<IReadOnlyList<ProbeResult>>([result]);
    }
}
