using System.Net;
using Dusiburg.AI.Ticket.Shared.Health;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Zammad.Health;

/// <summary>
/// Controlli 11 e 12 di <c>/api/health</c>: Zammad raggiungibile a <c>ZAMMAD_URL</c> e token valido. Il token di un
/// amministratore funziona ma è un avviso: le scritture vanno fatte con l'utente tecnico <c>ai-assistant</c> (PLAN §4).
/// Il client tipizzato si risolve a ogni esecuzione: il probe è un singleton e non deve tenersi un <see cref="HttpClient"/>.
/// </summary>
public sealed class ZammadHealthProbe(ZammadOptions options, IServiceProvider services) : IHealthProbe
{
    public const int ReachableNumber = 11;

    public const int TokenNumber = 12;

    public const string ReachableName = "Zammad raggiungibile";

    public const string TokenName = "Token Zammad";

    public async Task<IReadOnlyList<ProbeResult>> RunAsync(CancellationToken cancellationToken)
    {
        ZammadClient client = services.GetRequiredService<ZammadClient>();

        ProbeResult reachable = await ProbeResult.MeasureAsync(ReachableNumber, ReachableName, async () =>
        {
            HttpStatusCode status = await client.PingAsync(cancellationToken);

            return status == HttpStatusCode.OK
                ? (ProbeStatus.Ok, $"{options.BaseUrl} (HTTP 200)")
                : (ProbeStatus.Error, $"{options.BaseUrl} risponde HTTP {(int)status}: lo stack in WSL sta partendo o riavviando il railsserver");
        });

        if (reachable.Status == ProbeStatus.Error)
        {
            return [reachable, ProbeResult.Skipped(TokenNumber, TokenName, "Zammad non raggiungibile")];
        }

        if (!options.HasApiToken)
        {
            return
            [
                reachable,
                new ProbeResult(TokenNumber, TokenName, ProbeStatus.Error,
                    $"{ZammadOptions.Keys.ApiToken} non configurato: impostare Parameters:zammad-api-token negli user-secrets dell'AppHost", TimeSpan.Zero)
            ];
        }

        return [reachable, await ProbeResult.MeasureAsync(TokenNumber, TokenName, () => CheckTokenAsync(client, cancellationToken))];
    }

    private static async Task<(ProbeStatus, string)> CheckTokenAsync(ZammadClient client, CancellationToken cancellationToken)
    {
        try
        {
            ZammadUser user = await client.GetCurrentUserAsync(cancellationToken);
            string detail = $"utente {user.Login} (id {user.Id}), ruoli {string.Join(", ", user.Roles)}";

            return user.IsAdmin
                ? (ProbeStatus.Warning, $"{detail}: token con permessi di amministratore, da sostituire con quello di ai-assistant (Fase 4)")
                : (ProbeStatus.Ok, detail);
        }
        catch (ZammadApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            return (ProbeStatus.Error, $"token rifiutato da Zammad ({exception.Message}): rigenerarlo e aggiornare Parameters:zammad-api-token");
        }
    }
}
