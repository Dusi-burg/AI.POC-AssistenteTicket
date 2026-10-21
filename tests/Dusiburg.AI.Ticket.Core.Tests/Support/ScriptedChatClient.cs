using Dusiburg.AI.Ticket.Ai;
using Microsoft.Extensions.AI;

namespace Dusiburg.AI.Ticket.Core.Tests.Support;

/// <summary>
/// Modello finto (G12): risponde con i testi indicati, nell'ordine, per compito; registra messaggi e opzioni ricevuti.
/// Nessuna chiamata a Ollama.
/// </summary>
internal sealed class ScriptedChatClientFactory(IReadOnlyDictionary<ModelTask, Queue<string>> replies) : IChatClientFactory
{
    public List<(ModelTask Task, IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public static ScriptedChatClientFactory With(string[] classify, string[] draft) => new(new Dictionary<ModelTask, Queue<string>>
    {
        [ModelTask.Classify] = new(classify),
        [ModelTask.Draft] = new(draft)
    });

    public ChatModel Create(ModelTask task) =>
        new(new ScriptedChatClient(this, task), task, "stub", $"stub-{task.ToString().ToLowerInvariant()}", new ChatOptions { Temperature = 0.1f });

    private sealed class ScriptedChatClient(ScriptedChatClientFactory owner, ModelTask task) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            owner.Calls.Add((task, [.. messages], options));

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, owner.Next(task))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private string Next(ModelTask task) =>
        replies[task].TryDequeue(out string? reply) ? reply : throw new InvalidOperationException($"Nessuna risposta preparata per {task}.");
}
