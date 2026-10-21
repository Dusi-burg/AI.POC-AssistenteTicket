using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dusiburg.AI.Ticket.Zammad;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Cli.Tests.Support;

/// <summary>Richiesta ricevuta dal finto Zammad, con il corpo già letto.</summary>
internal sealed record RecordedRequest(HttpMethod Method, string Path, string? From, JsonObject? Body);

/// <summary>
/// Zammad finto con stato: tiene in memoria le risorse che bootstrap, seed e demo leggono e creano, così i test verificano
/// l'idempotenza (un secondo giro non crea nulla). Riproduce le regole dell'istanza vera che contano: token per utente, header
/// <c>From</c>, attributi in attesa di migrazione, segreto del webhook mascherato. Nessuna rete.
/// </summary>
internal sealed class FakeZammadServer : HttpMessageHandler
{
    public const string AdminToken = "admin-token";
    public const long AdminId = 3;

    private readonly Dictionary<string, List<JsonObject>> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _tokens = new(StringComparer.Ordinal) { [AdminToken] = AdminId };
    private readonly List<(long Id, long UserId, string Name, string Token)> _accessTokens = [];
    private readonly List<(HttpMethod Method, string PathPrefix)> _failures = [];
    private long _nextId = 1000;

    public FakeZammadServer()
    {
        Add("roles", new JsonObject { ["id"] = 1, ["name"] = "Admin" }, new JsonObject { ["id"] = 2, ["name"] = "Agent" }, new JsonObject { ["id"] = 3, ["name"] = "Customer" });
        Add("users", new JsonObject
        {
            ["id"] = AdminId, ["login"] = "admin@example.it", ["email"] = "admin@example.it", ["firstname"] = "Admin",
            ["role_ids"] = new JsonArray(1, 2), ["group_ids"] = new JsonObject { ["1"] = new JsonArray("full") }
        });
        Add("groups", new JsonObject { ["id"] = 1, ["name"] = "Users", ["active"] = true, ["shared_drafts"] = true });
        Add("settings", new JsonObject { ["id"] = 157, ["name"] = "import_mode", ["state_current"] = new JsonObject { ["value"] = false } });

        // Impostazioni dell'AI nativa con i default del 7.2: i suggerimenti dalla knowledge base nascono accesi.
        long settingId = 300;

        foreach ((string name, bool value) in new[]
        {
            ("ai_provider", false), ("ai_assistance_ticket_summary", false), ("ai_assistance_text_tools", false),
            ("ai_assistance_kb_answer_suggestions", true), ("ai_assistance_kb_answer_from_ticket_generation", true)
        })
        {
            Add("settings", new JsonObject { ["id"] = ++settingId, ["name"] = name, ["state_current"] = new JsonObject { ["value"] = value } });
        }
        Add("object_manager_attributes", new JsonObject { ["id"] = 1, ["name"] = "title", ["object"] = "Ticket", ["active"] = true, ["to_create"] = false, ["to_migrate"] = false });

        foreach (string empty in new[] { "organizations", "tag_list", "webhooks", "triggers", "tickets", "ticket_articles", "ai/provider_connections", "ai_agents" })
        {
            _resources[empty] = [];
        }
    }

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Se il container raggiunge Ollama: senza, Zammad rifiuta la connessione (422) e l'elenco dei modelli ha un errore.</summary>
    public bool OllamaReachable { get; set; } = true;

    /// <summary>Modelli presenti su Ollama, come li elenca Zammad.</summary>
    public List<string> OllamaModels { get; } = ["qwen3.5:9b", "embeddinggemma:latest"];

    /// <summary>Valore di <c>import_mode</c> a ogni modifica, nell'ordine.</summary>
    public List<bool> ImportModeChanges { get; } = [];

    public IReadOnlyList<JsonObject> Resource(string name) => _resources[name];

    /// <summary>Id numerico di un nodo, qualunque sia il tipo con cui è stato creato (int, long o letto dal JSON).</summary>
    public static long Id(JsonNode? node) => long.Parse(node!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    public void FailOn(HttpMethod method, string pathPrefix) => _failures.Add((method, pathPrefix));

    /// <summary>Registra un utente con il suo token (per esempio un agente non amministratore).</summary>
    public long AddUser(string login, long[] roleIds, string? token = null)
    {
        long id = ++_nextId;
        Add("users", new JsonObject { ["id"] = id, ["login"] = login, ["email"] = login, ["role_ids"] = new JsonArray([.. roleIds.Select(r => (JsonNode?)r)]) });

        if (token is not null)
        {
            _tokens[token] = id;
        }

        return id;
    }

    public ServiceProvider CreateServices(string token = AdminToken)
    {
        var settings = new Dictionary<string, string?> { [ZammadOptions.Keys.BaseUrl] = "http://zammad.test:8080", [ZammadOptions.Keys.ApiToken] = token };
        var services = new ServiceCollection();
        services.AddTicketZammad(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddHttpClient<ZammadClient>().ConfigurePrimaryHttpMessageHandler(() => this);

        return services.BuildServiceProvider();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri!.AbsolutePath["/api/v1/".Length..];
        string? from = request.Headers.TryGetValues("From", out IEnumerable<string>? values) ? values.Single() : null;
        JsonObject? body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;

        Requests.Add(new RecordedRequest(request.Method, path, from, body));

        if (_failures.Any(f => f.Method == request.Method && path.StartsWith(f.PathPrefix, StringComparison.Ordinal)))
        {
            return Json(HttpStatusCode.InternalServerError, new JsonObject { ["error"] = "errore simulato" });
        }

        long? caller = CallerId(request, from);

        if (caller is null)
        {
            return Json(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = "Invalid token!" });
        }

        return Route(request.Method, path, request.RequestUri.Query, body ?? [], caller.Value);
    }

    private HttpResponseMessage Route(HttpMethod method, string path, string query, JsonObject body, long caller)
    {
        string[] segments = path.Split('/');

        // Le risorse dell'AI nativa stanno sotto ai/: "ai/provider_connections" vale come un solo segmento.
        if (segments is ["ai", var aiResource, .. var rest])
        {
            segments = [$"ai/{aiResource}", .. rest];
        }

        return (method.Method, segments) switch
        {
            ("POST", ["ai/provider_connections"]) when !OllamaReachable => Unreachable(),
            ("PUT", ["ai/provider_connections", var id, "set_default"]) => SetDefault(long.Parse(id, System.Globalization.CultureInfo.InvariantCulture), body),
            ("POST", ["ai/provider_connections", _, "models"]) => Json(HttpStatusCode.OK, OllamaReachable
                ? new JsonObject { ["models"] = new JsonArray([.. OllamaModels.Select(m => (JsonNode?)new JsonObject { ["id"] = m })]), ["error"] = null }
                : new JsonObject { ["models"] = new JsonArray(), ["error"] = "Connection refused" }),
            ("GET", ["users", "me"]) => Json(HttpStatusCode.OK, Me(caller)),
            ("GET", ["user_access_token"]) => Json(HttpStatusCode.OK, new JsonObject
            {
                ["tokens"] = new JsonArray([.. _accessTokens.Where(t => t.UserId == caller).Select(t => (JsonNode?)new JsonObject { ["id"] = t.Id, ["name"] = t.Name })])
            }),
            ("POST", ["user_access_token"]) => CreateToken(caller, body),
            ("DELETE", ["user_access_token", var id]) => DeleteToken(long.Parse(id, System.Globalization.CultureInfo.InvariantCulture)),
            ("POST", ["object_manager_attributes_execute_migrations"]) => Migrate(),
            ("GET", [var resource]) => Json(HttpStatusCode.OK, Page(resource, query)),
            ("POST", [var resource]) => Json(HttpStatusCode.Created, Create(resource, body, caller)),
            ("PUT", [var resource, var id]) => Update(resource, long.Parse(id, System.Globalization.CultureInfo.InvariantCulture), body),
            _ => Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = $"percorso non previsto: {method} {path}" })
        };
    }

    private long? CallerId(HttpRequestMessage request, string? from)
    {
        string? token = request.Headers.Authorization?.Parameter?.Replace("token=", "", StringComparison.Ordinal);

        if (token is null || !_tokens.TryGetValue(token, out long user))
        {
            return null;
        }

        return from is null ? user : Id(_resources["users"].Single(u => string.Equals((string?)u["login"], from, StringComparison.OrdinalIgnoreCase)
            || string.Equals((string?)u["email"], from, StringComparison.OrdinalIgnoreCase))["id"]);
    }

    private JsonObject Me(long caller)
    {
        JsonObject user = _resources["users"].Single(u => FakeZammadServer.Id(u["id"]) == caller);
        IEnumerable<long> roles = user["role_ids"]?.AsArray().Select(r => FakeZammadServer.Id(r)) ?? [];

        return new JsonObject
        {
            ["id"] = caller, ["login"] = (string?)user["login"],
            ["roles"] = new JsonArray([.. roles.Select(r => (JsonNode?)(string?)_resources["roles"].Single(x => FakeZammadServer.Id(x["id"]) == r)["name"])])
        };
    }

    private HttpResponseMessage CreateToken(long caller, JsonObject body)
    {
        string token = $"token-{++_nextId}";
        _accessTokens.Add((_nextId, caller, (string)body["name"]!, token));
        _tokens[token] = caller;

        return Json(HttpStatusCode.OK, new JsonObject { ["token"] = token });
    }

    private HttpResponseMessage DeleteToken(long id)
    {
        var token = _accessTokens.Single(t => t.Id == id);
        _accessTokens.Remove(token);
        _tokens.Remove(token.Token);

        return Json(HttpStatusCode.OK, new JsonObject());
    }

    private HttpResponseMessage Migrate()
    {
        foreach (JsonObject attribute in _resources["object_manager_attributes"])
        {
            attribute["to_create"] = false;
            attribute["to_migrate"] = false;
        }

        return Json(HttpStatusCode.OK, new JsonObject());
    }

    /// <summary>Le risorse paginate restituiscono tutto alla prima pagina; il segreto del webhook è mascherato come nello Zammad vero.</summary>
    private JsonArray Page(string resource, string query)
    {
        if (query.Contains("page=", StringComparison.Ordinal) && !query.Contains("page=1&", StringComparison.Ordinal))
        {
            return [];
        }

        return new JsonArray([.. _resources[resource].Select(item =>
        {
            JsonObject copy = item.DeepClone().AsObject();

            if (resource == "webhooks")
            {
                copy["signature_token"] = "**********";
            }

            return (JsonNode?)copy;
        })]);
    }

    private JsonObject Create(string resource, JsonObject body, long caller)
    {
        JsonObject created = body.DeepClone().AsObject();
        created["id"] = ++_nextId;

        switch (resource)
        {
            case "object_manager_attributes":
                created["to_create"] = true;
                created["to_migrate"] = false;
                break;
            case "tickets":
                created["number"] = $"7{_nextId}";
                created["customer_id"] = caller;
                created.Remove("article");
                break;
            case "ticket_articles":
                created["created_by_id"] = caller;
                break;
            case "ai/provider_connections":
                // Come Zammad: la prima connessione serve anche gli embedding, con il modello raccomandato.
                bool first = _resources[resource].Count == 0;
                created["default_chat"] = first || (bool?)body["default_chat"] == true;
                created["default_embedding"] = first;
                created["config"]!["model_temperature_support"] = true;
                created["status"] = new JsonObject();
                break;
        }

        _resources[resource].Add(created);

        // Come Zammad 7.2.2: l'embedding della prima connessione si assegna con un secondo salvataggio, assente dalla risposta.
        if (resource == "ai/provider_connections")
        {
            JsonObject response = created.DeepClone().AsObject();
            response["default_embedding"] = false;

            return response;
        }

        return created;
    }

    private HttpResponseMessage Update(string resource, long id, JsonObject body)
    {
        JsonObject? item = _resources[resource].SingleOrDefault(r => FakeZammadServer.Id(r["id"]) == id);

        if (item is null)
        {
            return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = $"{resource}/{id} inesistente" });
        }

        foreach ((string key, JsonNode? value) in body)
        {
            item[key] = value?.DeepClone();
        }

        if (resource == "settings" && (string?)item["name"] == "import_mode")
        {
            ImportModeChanges.Add(item["state_current"]!["value"]!.GetValue<bool>());
        }

        return Json(HttpStatusCode.OK, item);
    }

    private HttpResponseMessage SetDefault(long id, JsonObject body)
    {
        JsonObject connection = _resources["ai/provider_connections"].Single(c => FakeZammadServer.Id(c["id"]) == id);
        connection[$"default_{(string)body["default"]!}"] = body["enabled"]?.GetValue<bool>() ?? true;

        return Json(HttpStatusCode.OK, connection);
    }

    private static HttpResponseMessage Unreachable() =>
        Json(HttpStatusCode.UnprocessableContent, new JsonObject { ["error"] = "Failed to open TCP connection to 172.24.176.1:11434 (Connection refused)" });

    private void Add(string resource, params JsonObject[] items)
    {
        if (!_resources.TryGetValue(resource, out List<JsonObject>? list))
        {
            _resources[resource] = list = [];
        }

        list.AddRange(items);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) => new(status)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
    };
}
