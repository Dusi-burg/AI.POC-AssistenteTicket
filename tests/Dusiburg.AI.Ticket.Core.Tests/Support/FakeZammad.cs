using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Dusiburg.AI.Ticket.Zammad.Api;

namespace Dusiburg.AI.Ticket.Core.Tests.Support;

/// <summary>Zammad finto: risposte per metodo e percorso (senza query), richieste registrate con il corpo.</summary>
internal sealed class FakeZammad : HttpMessageHandler
{
    private readonly Dictionary<(string Method, string Path), (HttpStatusCode Status, string Body)> _responses = [];

    public List<(string Method, string Path, string? Body)> Requests { get; } = [];

    public FakeZammad On(string method, string path, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses[(method, path)] = (status, body);

        return this;
    }

    public ZammadClient Client() => new(new HttpClient(this) { BaseAddress = new Uri("http://zammad.test:8080/") });

    public IEnumerable<(string Method, string Path, string? Body)> Writes => Requests.Where(r => r.Method != "GET");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath;
        Requests.Add((request.Method.Method, path, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

        (HttpStatusCode status, string body) = _responses.TryGetValue((request.Method.Method, path), out var response)
            ? response
            : (HttpStatusCode.NotFound, """{"error":"percorso non previsto dal test"}""");

        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return new HttpResponseMessage(status) { RequestMessage = request, Content = content };
    }
}
