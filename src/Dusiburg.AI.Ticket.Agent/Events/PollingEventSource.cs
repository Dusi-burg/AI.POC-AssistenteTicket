using Dusiburg.AI.Ticket.Core;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Zammad.Api;

namespace Dusiburg.AI.Ticket.Agent.Events;

/// <summary>
/// Sorgente polling (P1, alternativa al webhook con <c>TICKET_EVENT_SOURCE=polling</c>): ogni <c>POLLING_INTERVAL_SECONDS</c> i
/// ticket aggiornati dal cursore, poi i loro articoli pubblici del cliente creati dopo l'avvio (V9). La ricerca passa da
/// Elasticsearch con qualche secondo di ritardo, quindi il cursore arretra di <see cref="CursorOverlap"/> e i doppioni li scarta
/// l'idempotenza su (ticket, articolo). Anche il salvataggio della bozza aggiorna il ticket: non basta l'<c>updated_at</c>.
/// Sugli stessi ticket, risposte dell'operatore e chiusure fanno il controllo dell'esito (Fase 7), idempotente anche lui.
/// </summary>
public sealed class PollingEventSource(
    ZammadClient zammad, TicketEventIntake intake, AgentOptions options, TimeProvider timeProvider, ILogger<PollingEventSource> logger) : BackgroundService
{
    internal static readonly TimeSpan CursorOverlap = TimeSpan.FromSeconds(30);

    private const int PageSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.EventSource != TicketEventSourceKind.Polling)
        {
            return;
        }

        DateTimeOffset startedAt = timeProvider.GetUtcNow();
        DateTimeOffset cursor = startedAt;
        logger.LogInformation("Sorgente polling attiva ogni {Interval}", options.PollingInterval);

        using var timer = new PeriodicTimer(options.PollingInterval, timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                cursor = await PollOnceAsync(cursor, startedAt, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Polling di Zammad non riuscito: riprovo al prossimo giro");
            }
        }
    }

    /// <summary>Un giro: restituisce il nuovo cursore (l'ultimo <c>updated_at</c> visto).</summary>
    internal async Task<DateTimeOffset> PollOnceAsync(DateTimeOffset cursor, DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        IReadOnlyList<ZammadTicketDetail> tickets = await zammad.SearchTicketsUpdatedSinceAsync(cursor - CursorOverlap, PageSize, cancellationToken);

        foreach (ZammadTicketDetail ticket in tickets)
        {
            IReadOnlyList<ZammadArticleDetail> articles = await zammad.GetArticlesAsync(ticket.Id, cancellationToken);

            foreach (ZammadArticleDetail article in NewCustomerArticles(articles, notBefore))
            {
                await intake.AcceptAsync(new TicketEvent(ticket.Id, article.Id, article.Sender, article.Internal, RunTrigger.Polling), cancellationToken);
            }

            if (HasFeedbackEvent(ticket, articles, notBefore))
            {
                await intake.CheckOutcomesAsync(ticket.Id, cancellationToken);
            }
        }

        return NextCursor(cursor, tickets);
    }

    /// <summary>Una risposta pubblica dell'operatore dopo l'avvio o il ticket chiuso: si controlla l'esito delle bozze (Fase 7).</summary>
    internal static bool HasFeedbackEvent(ZammadTicketDetail ticket, IEnumerable<ZammadArticleDetail> articles, DateTimeOffset notBefore) =>
        ticket.IsClosed || articles.Any(a => a.IsAgentReply && a.CreatedAt >= notBefore);

    /// <summary>Solo gli articoli pubblici del cliente creati dopo l'avvio: quelli precedenti non sono "nuovi".</summary>
    internal static IEnumerable<ZammadArticleDetail> NewCustomerArticles(IEnumerable<ZammadArticleDetail> articles, DateTimeOffset notBefore) =>
        articles.Where(a => a.IsFromCustomer && a.CreatedAt >= notBefore);

    /// <summary>L'ultimo <c>updated_at</c> visto, mai indietro rispetto al cursore attuale.</summary>
    internal static DateTimeOffset NextCursor(DateTimeOffset cursor, IReadOnlyList<ZammadTicketDetail> tickets) =>
        tickets.Count == 0 ? cursor : new[] { cursor, tickets.Max(t => t.UpdatedAt) }.Max();
}
