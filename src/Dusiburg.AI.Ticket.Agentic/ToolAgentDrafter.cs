using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dusiburg.AI.Ticket.Ai;
using Dusiburg.AI.Ticket.Core.Context;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Core.Pipeline;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Shared.Knowledge;
using Dusiburg.AI.Ticket.Shared.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Dusiburg.AI.Ticket.Agentic;

/// <summary>
/// Modalità agente (Fase 9b): un <see cref="ChatClientAgent"/> con i tool di <c>ticket-mcp</c> dietro la guardia sceglie le fonti,
/// poi una seconda chiamata, senza tool, scrive la bozza nello schema della pipeline fissa con le sole fonti lette ammesse. Due
/// fasi come l'agente autorità di Trasporto: con Ollama lo schema di output vincola la generazione al JSON e il modello non chiama
/// i tool. Nella seconda fase la conversazione con i tool diventa testo: l'API di Anthropic rifiuta chiamate ai tool nella
/// cronologia di una richiesta che non li definisce.
/// </summary>
public sealed class ToolAgentDrafter(IChatClientFactory models, IToolCatalog catalog, ToolAgentOptions options, ILoggerFactory loggerFactory) : IToolAgentDrafter
{
    public const string AgentName = "ToolAgentDrafter";

    /// <summary>Estratto di una fonte nella risposta di <c>cerca_fonti</c> mostrata al modello.</summary>
    public const int ExcerptChars = 350;

    /// <summary>Testo di una fonte letta per intero (come una fonte del prompt della pipeline fissa).</summary>
    public const int SourceChars = 1_800;

    public const int ReleaseNoteChars = 300;
    public const int MaxReleaseNotes = 12;

    /// <summary>Un risultato di tool nella trascrizione della seconda fase.</summary>
    public const int TranscriptResultChars = 4_000;

    private static readonly JsonSerializerOptions ReadJson = TicketJson.Options;
    private static readonly JsonSerializerOptions TextJson = new(TicketJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public IToolAgentDrafter WithModels(IChatClientFactory factory) => new ToolAgentDrafter(factory, catalog, options, loggerFactory);

    public async Task<ToolAgentDraft> DraftAsync(IncomingTicket ticket, ModelClassification classification, CancellationToken cancellationToken)
    {
        var run = new ToolAgentRun(options.MaxToolCalls);

        await using IToolSession session = await catalog.OpenAsync(cancellationToken);
        List<AITool> tools = Guard(session.Tools, run, ticket);

        using ChatModel model = models.Create(ModelTask.Agent);
        (string instructions, string task) = PromptBuilder.ToolAgentWork(ticket, classification, options.MaxToolCalls);

        // Pila propria con il limite di iterazioni: oltre, il modello non riceve altri giri con i tool (la guardia rifiuta comunque).
        IChatClient client = model.ChatClient.AsBuilder()
            .UseFunctionInvocation(loggerFactory, invocation =>
            {
                invocation.MaximumIterationsPerRequest = options.MaxToolCalls + 1;
                invocation.AllowConcurrentInvocation = false;
            })
            .Build();

        ChatOptions workOptions = model.DefaultOptions.Clone();
        workOptions.ModelId = model.ModelId;
        workOptions.Instructions = instructions;
        workOptions.Tools = tools;

        var agent = new ChatClientAgent(client,
            new ChatClientAgentOptions { Name = AgentName, ChatOptions = workOptions, UseProvidedChatClientAsIs = true }, loggerFactory);
        AgentSession conversation = await agent.CreateSessionAsync(cancellationToken);
        AgentResponse work = await agent.RunAsync(task, conversation, null, cancellationToken);

        List<ChatMessage> prompt = [new(ChatRole.System, instructions), new(ChatRole.User, task)];
        ModelCall<ModelDraft> draft = await OutcomeAsync(model, prompt, work, run.ReadKeys, cancellationToken);

        return new ToolAgentDraft(draft, run.Evidence, run.Calls);
    }

    /// <summary>
    /// I tool dell'agente, ciascuno dietro la sua guardia. Prodotto, modalità, versione, email e numero del ticket li mette il
    /// codice: il modello non può cercare per un altro cliente. Se il server non offre un tool, l'agente non parte.
    /// </summary>
    internal static List<AITool> Guard(IReadOnlyList<AIFunction> available, ToolAgentRun run, IncomingTicket ticket)
    {
        string[] missing = [.. TicketToolNames.All.Where(name => available.All(t => t.Name != name))];

        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"Il server MCP non offre i tool {string.Join(", ", missing)}: la modalità agente non può partire.");
        }

        CustomerContext customer = ticket.Customer;
        Dictionary<string, ToolRule> rules = new()
        {
            [TicketToolNames.SearchSources] = new ToolRule
            {
                HiddenArguments = ["prodotto", "modalita", "versione"],
                Before = arguments =>
                {
                    arguments["prodotto"] = customer.ProductCode;
                    arguments["modalita"] = customer.Mode?.ToString();
                    arguments["versione"] = customer.Version;

                    return null;
                },
                OnSuccess = data => run.AddSearch(Deserialize<KnowledgeSearchResult>(data)),
                Present = data => PresentSearch(Deserialize<KnowledgeSearchResult>(data))
            },
            [TicketToolNames.ReadSource] = new ToolRule
            {
                Before = arguments => run.IsAllowed(GuardedTool.ArgumentText(arguments, "chiave"))
                    ? null
                    : new ToolRejection(GuardedTool.NotAllowed,
                        $"La fonte '{GuardedTool.ArgumentText(arguments, "chiave")}' non è uscita da un risultato di questo ticket: cercala prima con {TicketToolNames.SearchSources}."),
                OnSuccess = data =>
                {
                    KnowledgeDocument document = Deserialize<KnowledgeDocument>(data);
                    run.AddWhole(document.Source, document.Key);
                },
                Present = data => PresentDocument(Deserialize<KnowledgeDocument>(data))
            },
            [TicketToolNames.ReleaseNotes] = new ToolRule
            {
                HiddenArguments = ["prodotto"],
                Before = arguments =>
                {
                    if (customer.ProductCode is null)
                    {
                        return new ToolRejection(Shared.Errors.ErrorCodes.ValidationError, "Il prodotto del cliente non è noto: le note di rilascio non si possono filtrare.");
                    }

                    arguments["prodotto"] = customer.ProductCode;

                    return null;
                },
                OnSuccess = data =>
                {
                    foreach (ReleaseNoteItem note in ReleaseNotes(data).Take(MaxReleaseNotes))
                    {
                        run.AddWhole(ChunkSource.ReleaseNote, note.Code);
                    }
                },
                Present = data => PresentReleaseNotes(ReleaseNotes(data))
            },
            [TicketToolNames.CustomerHistory] = new ToolRule
            {
                HiddenArguments = ["email"],
                Before = arguments =>
                {
                    if (customer.ContactEmail is null)
                    {
                        return new ToolRejection(Shared.Errors.ErrorCodes.ValidationError, "L'email del referente non è nota: lo storico del cliente non è disponibile.");
                    }

                    arguments["email"] = customer.ContactEmail;

                    return null;
                },
                OnSuccess = data => run.Allow(Deserialize<CustomerHistory>(data).Tickets.Where(t => t.Indexed).Select(t => t.Number))
            },
            [TicketToolNames.SearchZammad] = new ToolRule
            {
                HiddenArguments = ["escludiNumero"],
                Before = arguments =>
                {
                    arguments["escludiNumero"] = ticket.Number;

                    return null;
                },
                OnSuccess = data =>
                {
                    if (data.TryGetProperty("tickets", out JsonElement tickets) && tickets.ValueKind == JsonValueKind.Array)
                    {
                        run.Allow(tickets.EnumerateArray()
                            .Select(t => t.TryGetProperty("historicalKey", out JsonElement key) && key.ValueKind == JsonValueKind.String ? key.GetString()! : string.Empty));
                    }
                }
            }
        };

        return [.. available.Where(t => rules.ContainsKey(t.Name)).Select(t => (AITool)new GuardedTool(t, run, rules[t.Name]))];
    }

    /// <summary>La bozza in JSON, con un nuovo tentativo se non è valida (come <see cref="TicketLanguageModel"/>).</summary>
    private static async Task<ModelCall<ModelDraft>> OutcomeAsync(
        ChatModel model, List<ChatMessage> prompt, AgentResponse work, IReadOnlyList<string> readKeys, CancellationToken cancellationToken)
    {
        ChatOptions outcomeOptions = model.DefaultOptions.Clone();
        outcomeOptions.ModelId = model.ModelId;
        outcomeOptions.ResponseFormat = ModelJson.DraftFormat(readKeys);

        List<ChatMessage> conversation = Merge([.. prompt, .. Transcript(work.Messages), new(ChatRole.User, PromptBuilder.ToolAgentOutcome(readKeys))]);
        string hash = TicketLanguageModel.PromptHash(prompt);

        ChatResponse response = await model.ChatClient.GetResponseAsync(conversation, outcomeOptions, cancellationToken);
        ParseResult<ModelDraft> parsed = ModelJson.Parse<ModelDraft>(response.Text);
        long? input = Sum(work.Usage?.InputTokenCount, response.Usage?.InputTokenCount);
        long? output = Sum(work.Usage?.OutputTokenCount, response.Usage?.OutputTokenCount);

        if (parsed.Succeeded)
        {
            return new ModelCall<ModelDraft>(parsed.Value!, model.ModelId, hash, 1, input, output);
        }

        conversation.Add(new ChatMessage(ChatRole.Assistant, response.Text));
        conversation.Add(new ChatMessage(ChatRole.User,
            $"La risposta non è un JSON valido secondo lo schema (errore: {parsed.Error}). Rispondi di nuovo solo con il JSON."));

        string firstError = parsed.Error ?? string.Empty;
        response = await model.ChatClient.GetResponseAsync(conversation, outcomeOptions, cancellationToken);
        parsed = ModelJson.Parse<ModelDraft>(response.Text);

        return parsed.Succeeded
            ? new ModelCall<ModelDraft>(parsed.Value!, model.ModelId, hash, 2,
                Sum(input, response.Usage?.InputTokenCount), Sum(output, response.Usage?.OutputTokenCount))
            : throw new InvalidModelOutputException(ModelTask.Agent.ToString(), $"{firstError}; {parsed.Error}");
    }

    /// <summary>
    /// La fase di lavoro come testo: testo del modello, chiamate ai tool con gli argomenti, risultati (tagliati). Le chiamate
    /// diventano messaggi dell'assistente, i risultati messaggi dell'utente.
    /// </summary>
    internal static IEnumerable<ChatMessage> Transcript(IEnumerable<ChatMessage> messages)
    {
        Dictionary<string, string> names = [];

        foreach (ChatMessage message in messages)
        {
            var assistant = new StringBuilder();
            var results = new StringBuilder();

            foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        names[call.CallId] = call.Name;
                        assistant.AppendLine($"[chiamata {call.Name} {JsonSerializer.Serialize(call.Arguments, TextJson)}]");
                        break;
                    case FunctionResultContent result:
                        string text = result.Result switch
                        {
                            null => "(nessun risultato)",
                            string s => s,
                            _ => JsonSerializer.Serialize(result.Result, TextJson)
                        };
                        results.AppendLine($"[risultato di {names.GetValueOrDefault(result.CallId, "tool")}]");
                        results.AppendLine(text.Length > TranscriptResultChars ? $"{text[..TranscriptResultChars]}…" : text);
                        break;
                    case TextContent { Text.Length: > 0 } textContent when message.Role == ChatRole.Assistant:
                        assistant.AppendLine(textContent.Text);
                        break;
                }
            }

            if (assistant.Length > 0)
            {
                yield return new ChatMessage(ChatRole.Assistant, assistant.ToString().TrimEnd());
            }

            if (results.Length > 0)
            {
                yield return new ChatMessage(ChatRole.User, results.ToString().TrimEnd());
            }
        }
    }

    /// <summary>Messaggi consecutivi dello stesso ruolo uniti in uno: alcuni provider vogliono i turni alternati.</summary>
    internal static List<ChatMessage> Merge(IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage> merged = [];

        foreach (ChatMessage message in messages)
        {
            if (merged.Count > 0 && merged[^1].Role == message.Role && message.Role != ChatRole.System)
            {
                merged[^1] = new ChatMessage(message.Role, $"{merged[^1].Text}\n\n{message.Text}");
            }
            else
            {
                merged.Add(message);
            }
        }

        return merged;
    }

    private static JsonNode PresentSearch(KnowledgeSearchResult result) => new JsonObject
    {
        ["fonti"] = new JsonArray([.. result.Sources.Select(s => (JsonNode)new JsonObject
        {
            ["chiave"] = s.Key,
            ["tipo"] = SourceLabel(s.Source),
            ["applicabilita"] = s.Applicability,
            ["distanza"] = s.VectorDistance,
            ["estratto"] = Cut(s.Excerpt, ExcerptChars)
        })]),
        ["nota"] = result.Sources.Count == 0
            ? "Nessuna fonte: prova con altre parole, oppure lo storico non ha questo problema."
            : $"Leggi per intero con {TicketToolNames.ReadSource} le fonti che sembrano lo stesso problema."
    };

    private static JsonNode PresentDocument(KnowledgeDocument document) => new JsonObject
    {
        ["chiave"] = document.Key,
        ["tipo"] = SourceLabel(document.Source),
        ["applicabilita"] = document.Applicability,
        ["testo"] = Cut(string.Join("\n", document.Chunks.Select(c => c.Text).Distinct()), SourceChars)
    };

    private static JsonNode PresentReleaseNotes(IReadOnlyList<ReleaseNoteItem> notes) => new JsonObject
    {
        ["note"] = new JsonArray([.. notes.Take(MaxReleaseNotes).Select(n => (JsonNode)new JsonObject
        {
            ["chiave"] = n.Code,
            ["versione"] = n.Version,
            ["tipo"] = n.Type switch { ReleaseNoteType.KnownIssue => "problema noto", ReleaseNoteType.Fix => "correzione", _ => "novità" },
            ["titolo"] = n.Title,
            ["testo"] = Cut(n.Body, ReleaseNoteChars)
        })]),
        ["totale"] = notes.Count
    };

    private static IReadOnlyList<ReleaseNoteItem> ReleaseNotes(JsonElement data) =>
        data.TryGetProperty("notes", out JsonElement notes) ? notes.Deserialize<List<ReleaseNoteItem>>(ReadJson) ?? [] : [];

    private static T Deserialize<T>(JsonElement data) =>
        data.Deserialize<T>(ReadJson) ?? throw new JsonException($"Risultato del tool non leggibile come {typeof(T).Name}.");

    private static string SourceLabel(ChunkSource source) => source switch
    {
        ChunkSource.HistoricalTicket => "ticket risolto",
        ChunkSource.KbArticle => "articolo della knowledge base",
        ChunkSource.ReleaseNote => "nota di rilascio",
        _ => source.ToString()
    };

    private static string Cut(string text, int max) => text.Length <= max ? text : $"{text[..max].TrimEnd()}…";

    private static long? Sum(long? first, long? second) => first is { } a && second is { } b ? a + b : first ?? second;
}
