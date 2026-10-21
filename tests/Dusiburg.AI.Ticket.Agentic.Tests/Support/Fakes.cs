using System.Text.Json;
using Dusiburg.AI.Ticket.Agentic;
using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Core.Context;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Knowledge;
using Dusiburg.AI.Ticket.Shared.Serialization;
using Microsoft.Extensions.AI;

namespace Dusiburg.AI.Ticket.Agentic.Tests.Support;

/// <summary>Tool finto con lo schema dichiarato: registra gli argomenti ricevuti e risponde con un JSON preparato o un'eccezione.</summary>
internal sealed class FakeTool(string name, string[] properties, Func<AIFunctionArguments, object?> handler) : AIFunction
{
    public override string Name { get; } = name;

    public override JsonElement JsonSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = properties.ToDictionary(p => p, _ => new { type = "string" }),
        required = properties
    });

    public List<Dictionary<string, string?>> Received { get; } = [];

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        Received.Add(arguments.ToDictionary(a => a.Key, a => GuardedTool.ArgumentText(arguments, a.Key)));

        return ValueTask.FromResult(handler(arguments));
    }

    public static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, TicketJson.Options);
}

/// <summary>I cinque tool di <c>ticket-mcp</c> con risposte di default, sostituibili per test.</summary>
internal sealed class FakeToolSet : IToolCatalog
{
    public FakeToolSet()
    {
        Search = new FakeTool(TicketToolNames.SearchSources, ["testo", "prodotto", "modalita", "versione"], _ => FakeTool.Json(SearchResult));
        Read = new FakeTool(TicketToolNames.ReadSource, ["chiave"], a => FakeTool.Json(new KnowledgeDocument(
            ChunkSource.HistoricalTicket, GuardedTool.ArgumentText(a, "chiave")!, "FF · on-premise · tutte le versioni", [new KnowledgeChunk(11, 1, "Causa: catena di certificati.")])));
        Notes = new FakeTool(TicketToolNames.ReleaseNotes, ["prodotto", "versioneDa", "versioneA"], _ => FakeTool.Json(new
        {
            productCode = "FF",
            notes = new[] { new ReleaseNoteItem("RN-FF-5.2.0-03", "FF", "5.2.0", ReleaseNoteType.KnownIssue, "Ricezione ferma", "Workaround: riavviare il Receiver.") }
        }));
        History = new FakeTool(TicketToolNames.CustomerHistory, ["email"], _ => FakeTool.Json(new CustomerHistory("Studio Ferretti",
            [new CustomerTicketItem("HT-2024-0100", "Errore TLS", "trasmissione", DateTimeOffset.Parse("2024-05-01T10:00:00Z"), TicketState.Resolved, true)])));
        Zammad = new FakeTool(TicketToolNames.SearchZammad, ["testo", "escludiNumero"], _ => FakeTool.Json(new
        {
            tickets = new[] { new { number = "71100", title = "Errore TLS", state = "closed", historicalKey = "HT-2024-0200" } }
        }));
    }

    public KnowledgeSearchResult SearchResult { get; set; } = new("FF", DeploymentMode.OnPrem, "5.1.2",
    [
        new KnowledgeSource(1, ChunkSource.HistoricalTicket, "HT-2025-0001", 0.03, 0.18, false, "FF · on-premise · tutte le versioni",
            "Errore FF-E2041: catena di certificati.", [new KnowledgeChunkRef(11, 0.18), new KnowledgeChunkRef(12, 0.25)])
    ]);

    public FakeTool Search { get; }

    public FakeTool Read { get; }

    public FakeTool Notes { get; }

    public FakeTool History { get; }

    public FakeTool Zammad { get; }

    public IReadOnlyList<AIFunction> All => [Search, Read, Notes, History, Zammad];

    public Task<IToolSession> OpenAsync(CancellationToken cancellationToken) => Task.FromResult<IToolSession>(new Session(All));

    private sealed class Session(IReadOnlyList<AIFunction> tools) : IToolSession
    {
        public IReadOnlyList<AIFunction> Tools { get; } = tools;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Modello finto per il compito Agent: risponde, nell'ordine, con i messaggi preparati; registra messaggi e opzioni.</summary>
internal sealed class ScriptedAgentModels(params Func<ChatMessage>[] replies) : IChatClientFactory
{
    private readonly Queue<Func<ChatMessage>> _replies = new(replies);

    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public ChatModel Create(ModelTask task) => new(new Client(this), task, "stub", "stub-agent", new ChatOptions { Temperature = 0.1f });

    public static Func<ChatMessage> Call(string id, string tool, object arguments) => () => new ChatMessage(ChatRole.Assistant,
        [new FunctionCallContent(id, tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments)))]);

    public static Func<ChatMessage> Text(string text) => () => new ChatMessage(ChatRole.Assistant, text);

    private sealed class Client(ScriptedAgentModels owner) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            owner.Calls.Add(([.. messages], options));

            return owner._replies.TryDequeue(out Func<ChatMessage>? reply)
                ? Task.FromResult(new ChatResponse(reply()) { Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10 } })
                : throw new InvalidOperationException("Nessuna risposta preparata.");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

internal static class TestTickets
{
    public static IncomingTicket Ferretti() => new(170, "71170", "Fatture non trasmesse: errore FF-E2041", 429,
        [new ThreadMessage(429, MessageAuthor.Customer, DateTimeOffset.Parse("2026-10-16T09:00:00Z"), "Da stamattina tutte le fatture vanno in errore FF-E2041.")],
        new CustomerContext("Studio Contabile Ferretti S.r.l.", "Giorgio Ferretti", "giorgio.ferretti@ferretti-contabilita.example.it", "FF", "5.1.2",
            DeploymentMode.OnPrem, SlaLevel.Plus, "Zammad"));
}
