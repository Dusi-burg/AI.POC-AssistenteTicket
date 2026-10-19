using System.CommandLine;
using System.Net;
using System.Text.RegularExpressions;
using Dusiburg.AI.Ticket.Core.Composition;
using Dusiburg.AI.Ticket.Zammad;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Cli.Commands;

/// <summary>
/// <c>reply &lt;ticket&gt;</c> e <c>close &lt;ticket&gt;</c> (fase-7.md §5): fanno ciò che farebbe l'operatore nell'interfaccia di
/// Zammad, con il token dell'amministratore. <c>reply</c> invia la bozza condivisa come nota pubblica, com'è o modificata, e poi
/// la cancella (come fa Zammad all'invio); i trigger della Fase 7 portano l'evento all'Agent, che registra l'esito.
/// </summary>
internal static partial class OperatorCommands
{
    /// <summary>Frase aggiunta da <c>--modifica</c>: poche parole, una distanza piccola.</summary>
    public const string EditSentence = "Resto a disposizione per qualsiasi chiarimento.";

    /// <summary>Testo di <c>--riscrivi</c>: una risposta scritta dall'operatore, senza nulla della bozza.</summary>
    public const string RewrittenBody =
        "<p>Buongiorno,</p><p>abbiamo preso in carico la segnalazione: un tecnico la contatterà in giornata per una verifica " +
        "in collegamento remoto.</p><p>Un saluto,</p>";

    public static IEnumerable<Command> Create(IServiceProvider services) => [Reply(services), Close(services)];

    private static Command Reply(IServiceProvider services)
    {
        var ticket = TicketArgument();
        var edit = new Option<bool>("--modifica") { Description = $"Aggiunge una frase prima della dicitura (\"{EditSentence}\")." };
        var rewrite = new Option<bool>("--riscrivi") { Description = "Sostituisce il testo con una risposta scritta dall'operatore." };
        var withoutDisclosure = new Option<bool>("--senza-dicitura") { Description = "Toglie la dicitura AI prima dell'invio." };
        var command = new Command("reply", "Invia come operatore la bozza condivisa del ticket, com'è o modificata.") { ticket, edit, rewrite, withoutDisclosure };

        command.SetAction(async (parse, cancellationToken) =>
        {
            ZammadClient zammad = services.GetRequiredService<ZammadClient>();
            long ticketId = await ResolveAsync(zammad, parse.GetValue(ticket)!, cancellationToken);
            string? draft = await zammad.GetSharedDraftBodyAsync(ticketId, cancellationToken);

            if (draft is null)
            {
                Console.Error.WriteLine($"Ticket {ticketId}: nessuna bozza condivisa (già inviata, o l'Agent non l'ha ancora pubblicata).");
                return TicketCli.ExitError;
            }

            string body = Edit(draft, parse.GetValue(edit), parse.GetValue(rewrite), parse.GetValue(withoutDisclosure));

            ZammadArticleDetail article = await zammad.CreateAsync<ZammadArticleDetail>(ZammadResources.Articles, new Dictionary<string, object?>
            {
                ["ticket_id"] = ticketId,
                ["body"] = body,
                ["content_type"] = "text/html",
                ["type"] = "note",
                ["internal"] = false
            }, cancellationToken);

            await zammad.DeleteSharedDraftAsync(ticketId, cancellationToken);

            Console.WriteLine($"Risposta inviata come articolo {article.Id} ({article.Sender}): {new Uri(services.GetRequiredService<ZammadOptions>().BaseUrl, $"#ticket/zoom/{ticketId}")}");
            Console.WriteLine("L'esito lo registra l'Agent (trigger 'risposta dell'operatore'); controllo manuale: POST /api/outcomes/check.");

            return 0;
        });

        return command;
    }

    private static Command Close(IServiceProvider services)
    {
        var ticket = TicketArgument();
        var command = new Command("close", "Chiude il ticket come farebbe l'operatore.") { ticket };

        command.SetAction(async (parse, cancellationToken) =>
        {
            ZammadClient zammad = services.GetRequiredService<ZammadClient>();
            long ticketId = await ResolveAsync(zammad, parse.GetValue(ticket)!, cancellationToken);

            await zammad.UpdateAsync<ZammadTicket>(ZammadResources.Tickets, ticketId, new { State = ZammadClient.StateClosed }, cancellationToken);
            Console.WriteLine($"Ticket {ticketId} chiuso.");

            return 0;
        });

        return command;
    }

    /// <summary>Le modifiche dell'operatore simulato sull'HTML della bozza, nell'ordine: riscrittura, aggiunta, dicitura tolta.</summary>
    internal static string Edit(string draftHtml, bool edit, bool rewrite, bool withoutDisclosure)
    {
        string disclosure = WebUtility.HtmlEncode(DraftComposer.AiDisclosure);
        int disclosureAt = draftHtml.IndexOf(disclosure, StringComparison.Ordinal);
        string html = draftHtml;

        if (rewrite)
        {
            html = RewrittenBody + (disclosureAt >= 0 ? $"<p><em>{disclosure}</em></p>" : "");
        }

        if (edit)
        {
            Match closing = DisclosureParagraph().Match(html);
            html = closing.Success ? html.Insert(closing.Index, $"<p>{EditSentence}</p>") : html + $"<p>{EditSentence}</p>";
        }

        return withoutDisclosure ? DisclosureParagraph().Replace(html, string.Empty) : html;
    }

    private static Argument<string> TicketArgument() =>
        new("ticket") { Description = "Numero del ticket in Zammad (es. 71164) oppure il suo id (URL #ticket/zoom/<id>)." };

    private static async Task<long> ResolveAsync(ZammadClient zammad, string reference, CancellationToken cancellationToken) =>
        await zammad.FindTicketIdByNumberAsync(reference, cancellationToken)
        ?? (long.TryParse(reference, out long id) ? id : throw new InvalidOperationException($"Ticket {reference} non trovato in Zammad."));

    /// <summary>Il paragrafo della dicitura AI come lo scrive <c>DraftComposer</c>: <c>&lt;p&gt;&lt;em&gt;…&lt;/em&gt;&lt;/p&gt;</c>.</summary>
    [GeneratedRegex(@"<p>\s*<em>\s*Risposta predisposta con il supporto[^<]*</em>\s*</p>")]
    private static partial Regex DisclosureParagraph();
}
