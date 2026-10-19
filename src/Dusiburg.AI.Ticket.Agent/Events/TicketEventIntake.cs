using System.Threading.Channels;
using Dusiburg.AI.Ticket.Core.Pipeline;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Agent.Events;

/// <summary>Un articolo di Zammad arrivato da una sorgente (P1): webhook, polling o avvio manuale.</summary>
public sealed record TicketEvent(long TicketId, long ArticleId, string? Sender, bool Internal, RunTrigger Trigger);

public enum IntakeOutcome
{
    Queued,

    /// <summary>(ticket, articolo) già ricevuto: doppia consegna del webhook o polling che rilegge.</summary>
    Duplicate,

    /// <summary>Non è un articolo pubblico del cliente: le note e le risposte dell'operatore non fanno partire l'agente.</summary>
    Ignored
}

public sealed record IntakeResult(IntakeOutcome Outcome, int? RunId);

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
/// Ingresso comune delle sorgenti di eventi (P1): filtro sul mittente (solo articoli pubblici del cliente, così l'agente non
/// riparte sulle proprie note), registrazione idempotente su (ticket, articolo), accodamento.
/// </summary>
public sealed class TicketEventIntake(IServiceScopeFactory scopes, TicketWorkQueue queue, ILogger<TicketEventIntake> logger)
{
    public async Task<IntakeResult> AcceptAsync(TicketEvent ticketEvent, CancellationToken cancellationToken)
    {
        if (ticketEvent.Internal || !string.Equals(ticketEvent.Sender, "Customer", StringComparison.Ordinal))
        {
            return new IntakeResult(IntakeOutcome.Ignored, null);
        }

        using IServiceScope scope = scopes.CreateScope();
        AgentRunService runs = scope.ServiceProvider.GetRequiredService<AgentRunService>();
        int? runId = await runs.TryCreateAsync(ticketEvent.TicketId, ticketEvent.ArticleId, ticketEvent.Trigger, cancellationToken);

        if (runId is not { } id)
        {
            logger.LogInformation("Articolo {ArticleId} del ticket {TicketId} già ricevuto ({Trigger}): scartato", ticketEvent.ArticleId, ticketEvent.TicketId, ticketEvent.Trigger);
            return new IntakeResult(IntakeOutcome.Duplicate, null);
        }

        queue.Enqueue(id);
        logger.LogInformation("Elaborazione {RunId} in coda: ticket {TicketId}, articolo {ArticleId} ({Trigger})", id, ticketEvent.TicketId, ticketEvent.ArticleId, ticketEvent.Trigger);

        return new IntakeResult(IntakeOutcome.Queued, id);
    }
}
