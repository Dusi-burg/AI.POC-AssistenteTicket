using System.CommandLine;
using Dusiburg.AI.Ticket.Core.Composition;
using Dusiburg.AI.Ticket.Core.Pipeline;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Zammad.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Dusiburg.AI.Ticket.Cli.Commands;

/// <summary>
/// <c>draft &lt;ticket&gt;</c>: la pipeline della Fase 6 a secco su un ticket di Zammad, per tarare il prompt. Non scrive né in
/// Zammad né nel database: la pubblicazione la fa solo l'Agent con l'utente <c>ai-assistant</c> (<c>POST /api/runs</c>).
/// </summary>
internal static class DraftCommand
{
    public static Command Create(IServiceProvider services)
    {
        var ticket = new Argument<string>("ticket") { Description = "Numero del ticket in Zammad (es. 71164) oppure il suo id (URL #ticket/zoom/<id>)." };
        var article = new Option<long?>("--articolo") { Description = "Articolo del cliente da elaborare; di default l'ultimo." };
        var command = new Command("draft", "Esegue la pipeline su un ticket di Zammad senza pubblicare nulla e mostra bozza, fonti e confidenza.") { ticket, article };

        command.SetAction(async (parse, cancellationToken) =>
        {
            using IServiceScope scope = services.CreateScope();
            TicketPipeline pipeline = scope.ServiceProvider.GetRequiredService<TicketPipeline>();

            string reference = parse.GetValue(ticket)!;
            long ticketId = await scope.ServiceProvider.GetRequiredService<ZammadClient>().FindTicketIdByNumberAsync(reference, cancellationToken)
                ?? (long.TryParse(reference, out long id) ? id : throw new InvalidOperationException($"Ticket {reference} non trovato in Zammad."));

            DraftProposal proposal = await pipeline.ProposeAsync(ticketId, parse.GetValue(article), cancellationToken);
            Print(proposal, pipeline.Options.ConfidenceMinForDraft);

            return 0;
        });

        return command;
    }

    private static void Print(DraftProposal proposal, double threshold)
    {
        var c = proposal.Confidence;
        var classification = proposal.Classification.Value;
        PipelineTimings t = proposal.Timings;

        Console.WriteLine($"Ticket {proposal.Ticket.Number} — {proposal.Ticket.Title}");
        Console.WriteLine($"Cliente: {proposal.Ticket.Customer.OrganizationName ?? "—"} ({proposal.Ticket.Customer.ProductCode ?? "?"} " +
            $"{proposal.Ticket.Customer.Version ?? "?"} {proposal.Ticket.Customer.Mode?.ToString() ?? "?"}, da {proposal.Ticket.Customer.Origin})");
        Console.WriteLine($"Classificazione: {classification.Category}, priorità {classification.Priority}, manipolazione {(classification.SuspectedManipulation ? "sì" : "no")}; " +
            $"mancano: {(classification.MissingInformation.Count == 0 ? "nulla" : string.Join("; ", classification.MissingInformation))}");
        Console.WriteLine($"Fonti del prompt: {string.Join(", ", proposal.Retrieval.PromptSources.Select(Describe))}");
        Console.WriteLine($"Citate: {string.Join(", ", proposal.Verification.Citations.Select(x => x.IsVerified ? x.Id : $"{x.Id} (NON VALIDA)"))}");

        if (proposal.Verification.UnsupportedTerms.Count > 0)
        {
            Console.WriteLine($"Termini non supportati: {string.Join(", ", proposal.Verification.UnsupportedTerms)}");
        }

        Console.WriteLine($"Modello: puoRispondere {proposal.ModelDraft.Value.CanAnswer}, autovalutazione {proposal.ModelDraft.Value.SelfAssessment:0.00}" +
            $"{(proposal.ModelDraft.Attempts > 1 ? ", JSON al secondo tentativo" : "")}{(proposal.Verification.SignatureRemoved ? ", firma tolta" : "")}");
        Console.WriteLine($"Confidenza {c.Confidence:0.00} (soglia {threshold:0.00}): retrieval {c.Retrieval:0.00}, citazioni {c.Citation:0.00}, " +
            $"copertura {c.Coverage:0.00} → {InternalNoteComposer.KindLabel(c.Kind)}");
        Console.WriteLine($"Tempi: contesto {t.Context.TotalSeconds:0.0} s, classificazione {t.Classification.TotalSeconds:0.0} s, retrieval {t.Retrieval.TotalSeconds:0.0} s, " +
            $"generazione {t.Generation.TotalSeconds:0.0} s, totale {t.Total.TotalSeconds:0.0} s");
        Console.WriteLine();
        Console.WriteLine(proposal.Draft.Text);
    }

    private static string Describe(RetrievedSource source) =>
        source.Best.VectorDistance is { } distance ? $"{source.Key} ({distance:0.000})" : source.Key;
}
