using Dusiburg.AI.Ticket.Core.Composition;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Zammad.Api;

namespace Dusiburg.AI.Ticket.Core.Publishing;

/// <summary>
/// Pubblicazione in Zammad (P3), con il token di <c>ai-assistant</c>: bozza condivisa nell'editor di risposta, nota interna,
/// tag. Nessun invio al cliente: la risposta la manda l'operatore. Prima la bozza (un <c>PUT</c>, ripetibile), poi nota e tag.
/// </summary>
public sealed class ZammadPublisher(ZammadClient zammad)
{
    public const string DraftTag = "ai-bozza";
    public const string LowConfidenceTag = "ai-bassa-confidenza";

    public async Task<IReadOnlyList<string>> PublishAsync(long ticketId, ComposedDraft draft, string noteHtml, CancellationToken cancellationToken)
    {
        if (!DraftComposer.HasDisclosure(draft.Html))
        {
            // P8: non deve succedere (la dicitura la aggiunge DraftComposer), ma una bozza senza dicitura non si pubblica.
            throw new InvalidOperationException("Bozza senza la dicitura AI: pubblicazione annullata.");
        }

        // Campi del form di risposta (fase-0.md §4 ter): senza canale email la risposta è una nota pubblica, visibile al cliente.
        await zammad.SaveSharedDraftAsync(ticketId, new Dictionary<string, object?>
        {
            ["body"] = draft.Html,
            ["content_type"] = "text/html",
            ["type"] = "note",
            ["internal"] = false
        }, cancellationToken);

        await zammad.CreateAsync<ZammadArticleDetail>(ZammadResources.Articles, new Dictionary<string, object?>
        {
            ["ticket_id"] = ticketId,
            ["subject"] = "Assistente AI",
            ["body"] = noteHtml,
            ["content_type"] = "text/html",
            ["type"] = "note",
            ["internal"] = true
        }, cancellationToken);

        List<string> tags = draft.Kind == DraftKind.Answer ? [DraftTag] : [DraftTag, LowConfidenceTag];

        foreach (string tag in tags)
        {
            await zammad.AddTicketTagAsync(ticketId, tag, cancellationToken);
        }

        return tags;
    }
}
