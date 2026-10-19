using System.Net;
using Dusiburg.AI.Ticket.Core.Composition;
using Dusiburg.AI.Ticket.Shared.Domain;
using Dusiburg.AI.Ticket.Zammad.Api;

namespace Dusiburg.AI.Ticket.Core.Publishing;

/// <summary>Che cosa è stato scritto in Zammad: i tag e la nota interna, la cui id fa da riferimento per il feedback (Fase 7).</summary>
public sealed record PublicationResult(IReadOnlyList<string> Tags, long NoteArticleId);

/// <summary>
/// Pubblicazione in Zammad (P3), con il token di <c>ai-assistant</c>: bozza condivisa nell'editor di risposta, nota interna,
/// tag. Nessun invio al cliente: la risposta la manda l'operatore. Prima la bozza (un <c>PUT</c>, ripetibile), poi nota e tag.
/// Dopo l'invio, la marcatura AI (P8, Fase 7).
/// </summary>
public sealed class ZammadPublisher(ZammadClient zammad)
{
    public const string DraftTag = "ai-bozza";
    public const string LowConfidenceTag = "ai-bassa-confidenza";
    public const string GeneratedTag = "ai-generata";
    public const string PartlyGeneratedTag = "ai-generata-in-parte";
    public const string DisclosureRemovedTag = "ai-dicitura-rimossa";

    public async Task<PublicationResult> PublishAsync(long ticketId, ComposedDraft draft, string noteHtml, CancellationToken cancellationToken)
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

        ZammadArticleDetail note = await CreateInternalNoteAsync(ticketId, noteHtml, cancellationToken);
        List<string> tags = draft.Kind == DraftKind.Answer ? [DraftTag] : [DraftTag, LowConfidenceTag];
        await AddTagsAsync(ticketId, tags, cancellationToken);

        return new PublicationResult(tags, note.Id);
    }

    /// <summary>
    /// Marcatura dopo l'invio (P8): <c>ai-generata</c> per una bozza inviata com'è, <c>ai-generata-in-parte</c> se modificata; se
    /// l'operatore ha tolto la dicitura anche <c>ai-dicitura-rimossa</c> e una nota interna, perché l'articolo inviato non si può
    /// più correggere. Una bozza scartata non riceve nulla. I tag si possono ripetere senza duplicarsi.
    /// </summary>
    public async Task<IReadOnlyList<string>> MarkOutcomeAsync(
        long ticketId, DraftOutcome outcome, long? replyArticleId, bool? disclosureKept, int runId, CancellationToken cancellationToken)
    {
        List<string> tags = outcome switch
        {
            DraftOutcome.SentUnchanged => [GeneratedTag],
            DraftOutcome.SentEdited => [PartlyGeneratedTag],
            _ => []
        };

        if (tags.Count > 0 && disclosureKept == false)
        {
            tags.Add(DisclosureRemovedTag);
            await CreateInternalNoteAsync(ticketId, DisclosureRemovedNote(replyArticleId, runId), cancellationToken);
        }

        await AddTagsAsync(ticketId, tags, cancellationToken);

        return tags;
    }

    internal static string DisclosureRemovedNote(long? replyArticleId, int runId) =>
        "<p><strong>Assistente AI — dicitura AI tolta</strong></p>" +
        $"<p>La risposta inviata{(replyArticleId is { } id ? $" (articolo {id})" : "")} nasce dalla bozza dell'assistente " +
        $"(elaborazione n. {runId}) ma non contiene la dicitura «{WebUtility.HtmlEncode(DraftComposer.AiDisclosure)}». " +
        "Un articolo inviato non si può correggere: valutare se informare il cliente con un nuovo messaggio.</p>";

    private async Task<ZammadArticleDetail> CreateInternalNoteAsync(long ticketId, string noteHtml, CancellationToken cancellationToken) =>
        await zammad.CreateAsync<ZammadArticleDetail>(ZammadResources.Articles, new Dictionary<string, object?>
        {
            ["ticket_id"] = ticketId,
            ["subject"] = "Assistente AI",
            ["body"] = noteHtml,
            ["content_type"] = "text/html",
            ["type"] = "note",
            ["internal"] = true
        }, cancellationToken);

    private async Task AddTagsAsync(long ticketId, IEnumerable<string> tags, CancellationToken cancellationToken)
    {
        foreach (string tag in tags)
        {
            await zammad.AddTicketTagAsync(ticketId, tag, cancellationToken);
        }
    }
}
