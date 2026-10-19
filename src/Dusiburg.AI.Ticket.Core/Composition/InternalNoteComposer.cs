using System.Globalization;
using System.Net;
using System.Text;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Core.Pipeline;
using Dusiburg.AI.Ticket.Core.Verification;
using Dusiburg.AI.Ticket.Rag.Retrieval;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Core.Composition;

/// <summary>
/// Nota interna per l'operatore (P3): che cosa è stato proposto e perché. Tipo di bozza, confidenza con i segnali, fonti con il
/// motivo e l'esito della verifica, informazioni mancanti, domande, avvisi. Mai visibile al cliente.
/// </summary>
public static class InternalNoteComposer
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public static string Compose(DraftProposal proposal, double threshold, Uri zammadUrl, string? traceUrl, int? runId)
    {
        ConfidenceAssessment confidence = proposal.Confidence;
        ModelClassification classification = proposal.Classification.Value;
        var html = new StringBuilder();

        html.Append($"<p><strong>Assistente AI — {KindLabel(confidence.Kind)}</strong> nella bozza della risposta (da rivedere prima dell'invio).</p>");
        html.Append($"<p>Confidenza <strong>{Number(confidence.Confidence)}</strong> (soglia {Number(threshold)}): retrieval {Number(confidence.Retrieval)} · " +
            $"citazioni {Number(confidence.Citation)} · copertura {Number(confidence.Coverage)} · autovalutazione " +
            $"{(confidence.SelfAssessment is { } self ? Number(self) : "—")}.</p>");
        html.Append($"<p>Decisione: {Encode(confidence.Reason)}.</p>");
        html.Append($"<p>Classificazione: {Encode(classification.Category)}, priorità {Encode(classification.Priority)}.</p>");

        if (classification.SuspectedManipulation)
        {
            html.Append("<p>⚠️ <strong>Possibile tentativo di manipolazione</strong>: il testo del cliente contiene istruzioni rivolte all'assistente, che sono state ignorate.</p>");
        }

        AppendSources(html, proposal, zammadUrl);
        AppendList(html, "Informazioni mancanti", classification.MissingInformation);
        AppendList(html, "Domande suggerite", proposal.ModelDraft.Value.Questions);
        AppendWarnings(html, proposal.Verification);

        html.Append($"<p><small>Modelli: {Encode(proposal.Classification.ModelId)} (classificazione), {Encode(proposal.ModelDraft.ModelId)} (bozza). " +
            $"Tempo {proposal.Timings.Total.TotalSeconds.ToString("0.0", Italian)} s" +
            (runId is { } id ? $", elaborazione n. {id}" : "") +
            (traceUrl is null ? "" : $", <a href=\"{Encode(traceUrl)}\">traccia</a>") + ".</small></p>");

        return html.ToString();
    }

    private static void AppendSources(StringBuilder html, DraftProposal proposal, Uri zammadUrl)
    {
        if (proposal.Retrieval.PromptSources.Count == 0)
        {
            html.Append("<p>Fonti: nessuna fonte pertinente nello storico, nella knowledge base o nelle note di rilascio.</p>");
            return;
        }

        Dictionary<string, VerifiedCitation> cited = proposal.Verification.Citations.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        html.Append("<p>Fonti proposte al modello:</p><ol>");

        foreach (RetrievedSource source in proposal.Retrieval.PromptSources)
        {
            string key = proposal.ZammadTicketOfSource.TryGetValue(source.Key, out long ticketId)
                ? $"<a href=\"{Encode(new Uri(zammadUrl, $"#ticket/zoom/{ticketId}").ToString())}\">{Encode(source.Key)}</a>"
                : Encode(source.Key);
            string distance = source.Best.VectorDistance is { } d ? $", distanza {Number(d)}" : "";
            string usage = cited.TryGetValue(source.Key, out VerifiedCitation? citation) ? $" — citata: {Encode(citation.Reason)}" : " — non citata";

            html.Append($"<li>{key} ({PromptBuilder.SourceLabel(source.Source)}{distance}){usage}</li>");
        }

        html.Append("</ol>");
    }

    private static void AppendWarnings(StringBuilder html, VerificationResult verification)
    {
        List<string> warnings = [.. verification.Citations.Where(c => !c.IsVerified).Select(c => $"citazione di una fonte non fornita: {c.Id}")];

        if (verification.UnsupportedTerms.Count > 0)
        {
            warnings.Add($"termini non presenti nelle fonti né nel ticket: {string.Join(", ", verification.UnsupportedTerms)}");
        }

        if (verification.SignatureRemoved)
        {
            warnings.Add("firma aggiunta dal modello tolta dal testo");
        }

        AppendList(html, "Avvisi della verifica", warnings);
    }

    private static void AppendList(StringBuilder html, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        html.Append($"<p>{Encode(title)}:</p><ul>");

        foreach (string item in items)
        {
            html.Append($"<li>{Encode(item)}</li>");
        }

        html.Append("</ul>");
    }

    public static string KindLabel(DraftKind kind) => kind switch
    {
        DraftKind.Answer => "risposta proposta",
        DraftKind.Clarification => "richiesta di chiarimenti",
        DraftKind.Escalation => "indicazione di escalation",
        _ => kind.ToString()
    };

    private static string Number(double value) => value.ToString("0.00", Italian);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
