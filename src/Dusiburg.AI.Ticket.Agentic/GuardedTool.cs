using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dusiburg.AI.Ticket.Core.Pipeline;
using Dusiburg.AI.Ticket.Shared.Errors;
using Dusiburg.AI.Ticket.Shared.Serialization;
using Dusiburg.AI.Ticket.Shared.Telemetry;
using Microsoft.Extensions.AI;

namespace Dusiburg.AI.Ticket.Agentic;

/// <summary>Una chiamata fermata dal codice prima del server: il modello riceve lo stesso involucro d'errore dei tool.</summary>
public sealed record ToolRejection(string Code, string Message);

/// <summary>
/// Ciò che il codice impone a un tool (come <c>ToolRule</c> di Trasporto): argomenti che il modello non vede perché li mette il
/// codice, controllo prima della chiamata, che cosa annotare dopo e che cosa mostrare al modello al posto del risultato completo.
/// </summary>
public sealed class ToolRule
{
    public IReadOnlyList<string> HiddenArguments { get; init; } = [];

    /// <summary>Prima della chiamata: inietta gli argomenti del codice e può rifiutare.</summary>
    public Func<AIFunctionArguments, ToolRejection?>? Before { get; init; }

    public Action<JsonElement>? OnSuccess { get; init; }

    /// <summary>La forma compatta che legge il modello (il contesto di un 9B è di 8K token); senza, il risultato com'è.</summary>
    public Func<JsonElement, JsonNode>? Present { get; init; }
}

/// <summary>
/// Guardia attorno a ogni tool offerto all'agente: limite di chiamate, regole del codice, errori trasformati in un risultato che
/// il modello legge (l'elaborazione non si ferma per un tool), registrazione della chiamata e span <c>agent.tool</c>.
/// </summary>
public sealed class GuardedTool : DelegatingAIFunction
{
    public const string RejectedPrefix = "rejected:";
    public const string LimitReached = "LIMIT_REACHED";
    public const string NotAllowed = "NOT_ALLOWED";

    private static readonly ActivitySource Telemetry = new(TicketTelemetry.Sources.Agentic);

    private static readonly JsonSerializerOptions ArgumentJson = new(TicketJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ToolAgentRun _run;
    private readonly ToolRule _rule;
    private readonly JsonElement _schema;

    public GuardedTool(AIFunction tool, ToolAgentRun run, ToolRule? rule = null)
        : base(tool)
    {
        _run = run;
        _rule = rule ?? new ToolRule();
        _schema = _rule.HiddenArguments.Count == 0 ? tool.JsonSchema : WithoutProperties(tool.JsonSchema, _rule.HiddenArguments);
    }

    public override JsonElement JsonSchema => _schema;

    /// <summary>Il testo di un argomento, qualunque forma gli abbia dato il modello (stringa, numero, JSON).</summary>
    public static string? ArgumentText(AIFunctionArguments arguments, string name) =>
        arguments.TryGetValue(name, out object? value) ? value switch
        {
            null => null,
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement json => json.GetRawText(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        } : null;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        using Activity? activity = Telemetry.StartActivity($"agent.tool {Name}");
        activity?.SetTag("tool.name", Name);

        long started = Stopwatch.GetTimestamp();
        string outcome;
        string? message = null;
        object? returned;

        if (_run.CallsToServer >= _run.MaxToolCalls)
        {
            (outcome, message, returned) = Reject(new ToolRejection(LimitReached,
                $"Limite di {_run.MaxToolCalls} chiamate raggiunto: non chiamare altri tool, scrivi che cosa hai trovato."));
        }
        else if (_rule.Before?.Invoke(arguments) is { } rejection)
        {
            (outcome, message, returned) = Reject(rejection);
        }
        else
        {
            try
            {
                object? value = await base.InvokeCoreAsync(arguments, cancellationToken);
                ToolResult result = ToolResultReader.Read(value);

                if (result.Succeeded)
                {
                    _rule.OnSuccess?.Invoke(result.Data);
                    outcome = "ok";
                    returned = _rule.Present is { } present ? JsonSerializer.SerializeToElement(present(result.Data), ArgumentJson) : value;
                }
                else
                {
                    outcome = $"error:{result.ErrorCode ?? ErrorCodes.Internal}";
                    message = result.ErrorMessage;
                    returned = value;
                }
            }
            // Un tool che non risponde è un fatto da riportare al modello, non la fine dell'elaborazione.
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                outcome = $"error:{ErrorCodes.UpstreamUnavailable}";
                message = "Tool non raggiungibile.";
                returned = Envelope(ErrorCodes.UpstreamUnavailable, message);
                activity?.AddException(exception);
            }
        }

        activity?.SetTag("tool.outcome", outcome);

        if (outcome != "ok")
        {
            activity?.SetStatus(ActivityStatusCode.Error, outcome);
        }

        _run.Record(new ToolCallRecord(Name, JsonSerializer.Serialize(arguments, ArgumentJson), outcome, Stopwatch.GetElapsedTime(started), message));

        return returned;
    }

    private static (string Outcome, string Message, object? Returned) Reject(ToolRejection rejection) =>
        ($"{RejectedPrefix}{rejection.Code}", rejection.Message, Envelope(rejection.Code, rejection.Message));

    private static JsonElement Envelope(string code, string message) =>
        JsonSerializer.SerializeToElement(ToolErrorResponse.Create(code, message), ArgumentJson);

    private static JsonElement WithoutProperties(JsonElement schema, IReadOnlyList<string> names)
    {
        JsonObject node = JsonNode.Parse(schema.GetRawText())?.AsObject() ?? [];

        if (node["properties"] is JsonObject properties)
        {
            foreach (string name in names)
            {
                properties.Remove(name);
            }
        }

        if (node["required"] is JsonArray required)
        {
            foreach (JsonNode? item in required.Where(i => i is not null && names.Contains(i.GetValue<string>())).ToList())
            {
                required.Remove(item);
            }
        }

        return JsonSerializer.SerializeToElement(node);
    }
}
