using System.Security.Cryptography;
using System.Text;
using Dusiburg.AI.Ticket.Ai;
using Microsoft.Extensions.AI;

namespace Dusiburg.AI.Ticket.Core.Drafting;

/// <summary>Risposta strutturata del modello con ciò che serve a registrarla: modello, hash del prompt, tentativi.</summary>
public sealed record ModelCall<T>(T Value, string ModelId, string PromptHash, int Attempts);

/// <summary>Il modello ha risposto due volte con un JSON non valido (PLAN §7.2): l'elaborazione finisce in errore, nessuna bozza.</summary>
public sealed class InvalidModelOutputException(string task, string error) : Exception($"{task}: JSON non valido due volte ({error}).")
{
    public const string Code = "MODEL_OUTPUT_INVALID";
}

/// <summary>
/// Le due chiamate al modello (classificazione e bozza), ciascuna col provider del suo compito (<c>MODEL_CLASSIFY_PROVIDER</c>,
/// <c>MODEL_DRAFT_PROVIDER</c>). JSON con schema; se non è valido un retry con l'errore, poi <see cref="InvalidModelOutputException"/>.
/// </summary>
public sealed class TicketLanguageModel(IChatClientFactory factory)
{
    public Task<ModelCall<ModelClassification>> ClassifyAsync(List<ChatMessage> messages, CancellationToken cancellationToken) =>
        CallAsync<ModelClassification>(ModelTask.Classify, messages, ModelJson.ClassificationFormat, cancellationToken);

    public Task<ModelCall<ModelDraft>> DraftAsync(List<ChatMessage> messages, IReadOnlyList<string> sourceIds, CancellationToken cancellationToken) =>
        CallAsync<ModelDraft>(ModelTask.Draft, messages, ModelJson.DraftFormat(sourceIds), cancellationToken);

    private async Task<ModelCall<T>> CallAsync<T>(ModelTask task, List<ChatMessage> messages, ChatResponseFormat format, CancellationToken cancellationToken)
        where T : class
    {
        using ChatModel model = factory.Create(task);
        ChatOptions options = model.DefaultOptions.Clone();
        options.ModelId = model.ModelId;
        options.ResponseFormat = format;

        string hash = PromptHash(messages);
        List<ChatMessage> conversation = [.. messages];
        ChatResponse response = await model.ChatClient.GetResponseAsync(conversation, options, cancellationToken);
        ParseResult<T> parsed = ModelJson.Parse<T>(response.Text);

        if (parsed.Succeeded)
        {
            return new ModelCall<T>(parsed.Value!, model.ModelId, hash, 1);
        }

        conversation.Add(new ChatMessage(ChatRole.Assistant, response.Text));
        conversation.Add(new ChatMessage(ChatRole.User,
            $"La risposta non è un JSON valido secondo lo schema (errore: {parsed.Error}). Rispondi di nuovo solo con il JSON."));

        string firstError = parsed.Error ?? string.Empty;
        response = await model.ChatClient.GetResponseAsync(conversation, options, cancellationToken);
        parsed = ModelJson.Parse<T>(response.Text);

        return parsed.Succeeded
            ? new ModelCall<T>(parsed.Value!, model.ModelId, hash, 2)
            : throw new InvalidModelOutputException(task.ToString(), $"{firstError}; {parsed.Error}");
    }

    /// <summary>SHA-256 dei messaggi (ruolo e testo): due bozze con lo stesso hash hanno avuto lo stesso prompt.</summary>
    internal static string PromptHash(IEnumerable<ChatMessage> messages)
    {
        string text = string.Join("\n␞\n", messages.Select(m => $"{m.Role}: {m.Text}"));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
