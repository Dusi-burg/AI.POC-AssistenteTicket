using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Dusiburg.AI.Ticket.Core.Drafting;

public sealed record ParseResult<T>(T? Value, string? Error)
{
    public bool Succeeded => Value is not null;
}

/// <summary>
/// Schema JSON generato dai tipi (come Sinistri), con i valori ammessi iniettati: categorie e priorità fisse, e per le fonti
/// citate **solo gli id delle fonti del prompt**. Con Ollama lo schema vincola la generazione, quindi il modello non può
/// citare una fonte che non ha ricevuto; la verifica a valle resta, perché un altro provider può non rispettarlo.
/// </summary>
public static class ModelJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    public static readonly IReadOnlyList<string> Priorities = ["bassa", "normale", "alta"];

    public static ChatResponseFormat ClassificationFormat { get; } = ChatResponseFormat.ForJsonSchema(
        WithEnums(Schema<ModelClassification>(), ("categoria", TicketCategories.All), ("priorita", Priorities)),
        nameof(ModelClassification), "Classificazione di un ticket di assistenza");

    /// <summary>Schema della bozza con gli id delle fonti ammessi; senza fonti l'elenco delle citazioni deve restare vuoto.</summary>
    public static ChatResponseFormat DraftFormat(IReadOnlyList<string> sourceIds)
    {
        JsonNode schema = Schema<ModelDraft>();
        JsonObject sources = schema["properties"]!["fonti"]!.AsObject();

        if (sourceIds.Count == 0)
        {
            sources["maxItems"] = 0;
        }
        else
        {
            sources["items"]!["properties"]!["id"]!.AsObject()["enum"] = new JsonArray([.. sourceIds.Select(id => (JsonNode)id)]);
        }

        return ChatResponseFormat.ForJsonSchema(ToElement(schema), nameof(ModelDraft), "Bozza di risposta a un ticket di assistenza");
    }

    /// <summary>JSON valido secondo il tipo; tollera un blocco <c>```json</c> attorno, che alcuni modelli aggiungono.</summary>
    public static ParseResult<T> Parse<T>(string text)
        where T : class
    {
        string json = text.Trim();

        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            int start = json.IndexOf('\n', StringComparison.Ordinal);
            int end = json.LastIndexOf("```", StringComparison.Ordinal);
            json = start > 0 && end > start ? json[(start + 1)..end].Trim() : json;
        }

        try
        {
            T? value = JsonSerializer.Deserialize<T>(json, Options);

            return value is null ? new ParseResult<T>(null, "risposta vuota") : new ParseResult<T>(value, null);
        }
        catch (JsonException exception)
        {
            return new ParseResult<T>(null, exception.Message);
        }
    }

    private static JsonNode Schema<T>() =>
        JsonNode.Parse(AIJsonUtilities.CreateJsonSchema(typeof(T), serializerOptions: Options).GetRawText())!;

    private static JsonElement WithEnums(JsonNode schema, params (string Property, IReadOnlyList<string> Values)[] enums)
    {
        foreach ((string property, IReadOnlyList<string> values) in enums)
        {
            schema["properties"]![property]!.AsObject()["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]);
        }

        return ToElement(schema);
    }

    private static JsonElement ToElement(JsonNode schema) => JsonDocument.Parse(schema.ToJsonString()).RootElement.Clone();
}
