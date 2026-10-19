using System.Threading.Channels;
using Dusiburg.AI.Ticket.Core.Feedback;
using Dusiburg.AI.Ticket.Core.Pipeline;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Zammad.Api;

namespace Dusiburg.AI.Ticket.Agent.Events;

/// <summary>
/// Un evento di Zammad arrivato da una sorgente (P1): un articolo (webhook, polling o avvio manuale) oppure, senza articolo, un
/// cambio del ticket come la chiusura (<paramref name="TicketState"/>).
/// </summary>
public sealed record TicketEvent(long TicketId, long? ArticleId, string? Sender, bool Internal, RunTrigger Trigger, string? TicketState = null)
{
    public bool IsCustomerArticle => ArticleId is not null && !Internal && string.Equals(Sender, ZammadClient.SenderCustomer, StringComparison.Ordinal);

    public bool IsAgentReply => ArticleId is not null && !Internal && string.Equals(Sender, ZammadClient.SenderAgent, StringComparison.Ordinal);

    public bool IsClosure => string.Equals(TicketState, ZammadClient.StateClosed, StringComparison.Ordinal);
}

public enum IntakeOutcome
{
    Queued,

    /// <summary>(ticket, articolo) già ricevuto: doppia consegna del webhook o polling che rilegge.</summary>
    Duplicate,

    /// <summary>Risposta dell'operatore o chiusura: controllato l'esito delle bozze del ticket (Fase 7).</summary>
    OutcomeChecked,

    /// <summary>Né un articolo pubblico del cliente né un evento di feedback: le note interne non fanno partire nulla.</summary>
    Ignored
}

/// <param name="Resolved">Esiti registrati da questo evento (solo per <see cref="IntakeOutcome.OutcomeChecked"/>).</param>
public sealed record IntakeResult(IntakeOutcome Outcome, int? RunId, IReadOnlyList<ResolvedOutcome>? Resolved = null);

/// <summary>
/// Coda delle elaborazioni (P2): un solo consumatore (<see cref="TicketWorker"/>), perché la GPU è seriale. Contiene gli id di
/// <c>agent.AgentRun</c>, così una consegna è registrata prima di essere in coda e sopravvive a un riavvio dell'Agent.
/// </summary>
public sealed class TicketWorkQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<int> Reader => _channel.Reader;

    public void Enqueue(int runId)
    {
        if (!_channel.Writer.TryWrite(runId))
        {
            throw new InvalidOperationException($"Coda chiusa: elaborazione {runId} non accodata.");
        }
    }
}

/// <summary>
/// Ingresso comune delle sorgenti di eventi (P1). Un articolo pubblico del cliente si registra in modo idempotente su (ticket,
/// articolo) e va in coda. Una risposta pubblica dell'operatore o la chiusura del ticket fanno il controllo dell'esito (Fase 7)
/// subito, senza passare dalla coda: non usa il modello e non deve aspettare un'elaborazione in corso. Il resto (note interne,
/// anche quelle dell'assistente) si ignora, così l'agente non riparte sulle proprie scritture.
/// </summary>
public sealed class TicketEventIntake(IServiceScopeFactory scopes, TicketWorkQueue queue, ILogger<TicketEventIntake> logger)
{
    public async Task<IntakeResult> AcceptAsync(TicketEvent ticketEvent, CancellationToken cancellationToken)
    {
        if (ticketEvent.IsCustomerArticle)
        {
            return await EnqueueAsync(ticketEvent, ticketEvent.ArticleId!.Value, cancellationToken);
        }

        if (ticketEvent.IsAgentReply || ticketEvent.IsClosure)
        {
            IReadOnlyList<ResolvedOutcome> resolved = await CheckOutcomesAsync(ticketEvent.TicketId, cancellationToken);

            return new IntakeResult(IntakeOutcome.OutcomeChecked, null, resolved);
        }

        return new IntakeResult(IntakeOutcome.Ignored, null);
    }

    /// <summary>Esito delle bozze in attesa del ticket; nessuna chiamata a Zammad se non ce ne sono.</summary>
    public async Task<IReadOnlyList<ResolvedOutcome>> CheckOutcomesAsync(long ticketId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopes.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<DraftOutcomeService>().CheckTicketAsync(ticketId, cancellationToken);
    }

    private async Task<IntakeResult> EnqueueAsync(TicketEvent ticketEvent, long articleId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopes.CreateScope();
        AgentRunService runs = scope.ServiceProvider.GetRequiredService<AgentRunService>();
        int? runId = await runs.TryCreateAsync(ticketEvent.TicketId, articleId, ticketEvent.Trigger, cancellationToken);

        if (runId is not { } id)
        {
            logger.LogInformation("Articolo {ArticleId} del ticket {TicketId} già ricevuto ({Trigger}): scartato", articleId, ticketEvent.TicketId, ticketEvent.Trigger);
            return new IntakeResult(IntakeOutcome.Duplicate, null);
        }

        queue.Enqueue(id);
        logger.LogInformation("Elaborazione {RunId} in coda: ticket {TicketId}, articolo {ArticleId} ({Trigger})", id, ticketEvent.TicketId, articleId, ticketEvent.Trigger);

        return new IntakeResult(IntakeOutcome.Queued, id);
    }
}
