using System.Net.Http.Headers;
using Dusiburg.AI.Ticket.Shared.Health;
using Dusiburg.AI.Ticket.Zammad.Api;
using Dusiburg.AI.Ticket.Zammad.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Zammad;

public static class ZammadServiceCollectionExtensions
{
    /// <summary>Opzioni, client tipizzato con indirizzo e token, probe di <c>/api/health</c>.</summary>
    public static IServiceCollection AddTicketZammad(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => ZammadOptions.FromConfiguration(configuration));

        services.AddHttpClient<ZammadClient>((provider, http) =>
        {
            ZammadOptions options = provider.GetRequiredService<ZammadOptions>();

            http.BaseAddress = options.BaseUrl;
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (options.HasApiToken)
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", $"token={options.ApiToken}");
            }
        });

        services.AddSingleton<IHealthProbe, ZammadHealthProbe>();

        return services;
    }
}
