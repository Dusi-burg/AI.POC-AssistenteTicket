using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Dusiburg.AI.Ticket.AppHost;

/// <summary>
/// Stack Zammad in WSL (G7, fase-1.md §3): dove sta la configurazione, quale distro e quale tag dello stack ufficiale usare.
/// Le manopole stanno nella sezione <c>Zammad</c> della configurazione dell'AppHost.
/// </summary>
internal sealed record ZammadStack(bool Managed, bool StopOnExit, string WslDistro, string StackTag, string ComposeDirectory, string BrowserUrl)
{
    public const string ResourceName = "zammad-wsl";

    public const string HealthCheckName = "zammad-http";

    /// <summary>Nome del progetto compose: stessi volumi dell'installazione della Fase 0.</summary>
    public const string ProjectName = "zammad";

    public static ZammadStack FromConfiguration(IConfiguration configuration, string appHostDirectory) => new(
        Managed: !bool.TryParse(configuration["Zammad:Managed"], out bool managed) || managed,
        StopOnExit: !bool.TryParse(configuration["Zammad:StopOnExit"], out bool stop) || stop,
        WslDistro: configuration["Zammad:WslDistro"] is { Length: > 0 } distro ? distro : "Ubuntu-26.04",
        StackTag: configuration["Zammad:StackTag"] is { Length: > 0 } tag ? tag : "v17.5.2",
        ComposeDirectory: Path.GetFullPath(configuration["Zammad:ComposeDir"] is { Length: > 0 } directory
            ? directory
            : Path.Combine(appHostDirectory, "..", "..", "infra", "zammad")),
        BrowserUrl: configuration[ZammadSettings.BaseUrlKey] is { Length: > 0 } url ? url : ZammadSettings.DefaultBaseUrl);

    /// <summary><c>C:\Dev\x</c> → <c>/mnt/c/Dev/x</c>: il percorso con cui WSL vede un file del repository.</summary>
    public static string ToWslPath(string windowsPath)
    {
        string fullPath = Path.GetFullPath(windowsPath);

        return fullPath.Length >= 2 && fullPath[1] == ':'
            ? $"/mnt/{char.ToLowerInvariant(fullPath[0])}{fullPath[2..].Replace('\\', '/')}"
            : throw new InvalidOperationException($"Il percorso '{fullPath}' non è su un'unità di Windows: WSL non saprebbe raggiungerlo.");
    }
}

internal static class ZammadStackExtensions
{
    /// <summary>
    /// <c>wsl -d &lt;distro&gt; -- bash infra/zammad/zammad-up.sh</c> come risorsa eseguibile (pattern <c>rabbitmq-wsl</c> di O2C),
    /// sana quando la pagina di Zammad risponde 200.
    /// </summary>
    public static IResourceBuilder<ExecutableResource> AddZammadStack(this IDistributedApplicationBuilder builder, ZammadStack stack)
    {
        string script = ZammadStack.ToWslPath(Path.Combine(stack.ComposeDirectory, "zammad-up.sh"));
        string envFile = ZammadStack.ToWslPath(Path.Combine(stack.ComposeDirectory, "zammad.env"));

        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            ZammadStack.HealthCheckName, _ => new ZammadHttpHealthCheck(new Uri(stack.BrowserUrl)), failureStatus: null, tags: null));

        // L'arresto dei container alla chiusura lo fa lo script (trap su SIGHUP): l'AppHost fermato da "aspire stop" o dal
        // debugger non esegue ApplicationStopping (verificato in Fase 1), mentre il processo in WSL il segnale lo riceve sempre.
        return builder.AddExecutable(ZammadStack.ResourceName, "wsl", builder.AppHostDirectory,
                "-d", stack.WslDistro, "--", "bash", script, envFile, stack.StackTag, stack.StopOnExit ? "stop" : "keep")
            .WithHealthCheck(ZammadStack.HealthCheckName);
    }
}

/// <summary>Zammad è pronto quando nginx risponde 200 sulla pagina iniziale (durante il riavvio del railsserver risponde 502).</summary>
internal sealed class ZammadHttpHealthCheck(Uri url) : IHealthCheck
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy($"{url} HTTP {(int)response.StatusCode}")
                : HealthCheckResult.Unhealthy($"{url} HTTP {(int)response.StatusCode}");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy($"{url} non raggiungibile: {exception.Message}");
        }
    }
}
