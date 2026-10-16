using System.Net;
using System.Text;
using Dusiburg.AI.Ticket.Zammad;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Zammad.Tests.Support;

/// <summary>Zammad finto: una risposta fissa per percorso e l'elenco delle richieste ricevute, con i loro header.</summary>
internal sealed class FakeZammadHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Corpi delle richieste, letti alla ricezione: il client chiude la richiesta dopo l'invio.</summary>
    public List<string?> Bodies { get; } = [];

    /// <param name="pathAndQuery">Percorso relativo alla radice, per esempio <c>/api/v1/users/me?expand=true</c>.</param>
    public FakeZammadHandler Respond(string pathAndQuery, HttpStatusCode status, string body = "")
    {
        _responses[pathAndQuery] = (status, body);

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));

        (HttpStatusCode status, string body) = _responses.TryGetValue(request.RequestUri!.PathAndQuery, out var response)
            ? response
            : (HttpStatusCode.NotFound, """{"error":"percorso non previsto dal test"}""");

        return new HttpResponseMessage(status)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, body.StartsWith('{') || body.StartsWith('[') ? "application/json" : "text/html")
        };
    }

    /// <summary>Servizi come nell'Agent (<see cref="ZammadServiceCollectionExtensions.AddTicketZammad"/>), con questo handler al posto della rete.</summary>
    public ServiceProvider CreateServices(string? apiToken)
    {
        var settings = new Dictionary<string, string?> { [ZammadOptions.Keys.BaseUrl] = "http://zammad.test:8080" };

        if (apiToken is not null)
        {
            settings[ZammadOptions.Keys.ApiToken] = apiToken;
        }

        var services = new ServiceCollection();
        services.AddTicketZammad(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddHttpClient<Api.ZammadClient>().ConfigurePrimaryHttpMessageHandler(() => this);

        return services.BuildServiceProvider();
    }
}
