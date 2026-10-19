using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Core.Context;

/// <summary>Un messaggio pubblico del thread, già pulito di firma, citazioni e disclaimer.</summary>
public sealed record ThreadMessage(long ArticleId, MessageAuthor Author, DateTimeOffset SentAt, string Text);

/// <summary>
/// Contesto del cliente (P4): dai campi personalizzati dell'organizzazione in Zammad, oppure da <c>support.Customer</c> per dominio
/// email. Ogni campo è nullo se non noto: prodotto, versione e modalità sconosciuti non filtrano il retrieval.
/// </summary>
public sealed record CustomerContext(
    string? OrganizationName, string ContactName, string? ContactEmail, string? ProductCode, string? Version, DeploymentMode? Mode, SlaLevel? Sla,
    string Origin)
{
    public string ContactFirstName => ContactName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ContactName;
}

/// <summary>Il ticket di Zammad da elaborare: titolo, thread pubblico e articolo del cliente che ha fatto partire l'elaborazione.</summary>
public sealed record IncomingTicket(
    long ZammadTicketId,
    string Number,
    string Title,
    long TriggerArticleId,
    IReadOnlyList<ThreadMessage> Thread,
    CustomerContext Customer)
{
    public IEnumerable<ThreadMessage> CustomerMessages => Thread.Where(m => m.Author == MessageAuthor.Customer);
}
