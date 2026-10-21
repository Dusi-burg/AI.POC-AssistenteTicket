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
        AppendToolCalls(html, proposal);
        AppendList(html, "Informazioni mancanti", classification.MissingInformation);
        AppendList(html, "Domande suggerite", proposal.ModelDraft.Value.Questions);
        AppendWarnings(html, proposal.Verification);

        html.Append($"<p><small>Modelli: {Encode(proposal.Classification.ModelId)} (classificazione), {Encode(proposal.ModelDraft.ModelId)} " +
            $"({(proposal.Mode == PipelineMode.Agent ? "agente con i tool" : "bozza")}). " +
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
        html.Append(proposal.Mode == PipelineMode.Agent ? "<p>Fonti lette dall'agente con i tool:</p><ol>" : "<p>Fonti proposte al modello:</p><ol>");

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

    /// <summary>Modalità agente (Fase 9b): quali tool ha chiamato, con quali argomenti, e quali chiamate ha fermato la guardia.</summary>
    private static void AppendToolCalls(StringBuilder html, DraftProposal proposal)
    {
        if (proposal.Mode != PipelineMode.Agent)
        {
            return;
        }

        if (proposal.Calls.Count == 0)
        {
            html.Append("<p>Modalità agente: <strong>nessuna chiamata ai tool</strong>, quindi nessuna fonte.</p>");
            return;
        }

        html.Append($"<p>Modalità agente: {proposal.Calls.Count} chiamate ai tool.</p><ol>");

        foreach (ToolCallRecord call in proposal.Calls)
        {
            string outcome = call.Succeeded ? "" : $" — {Encode(call.Outcome)}{(call.Message is { Length: > 0 } m ? $": {Encode(m)}" : "")}";

            html.Append($"<li><code>{Encode(call.ToolName)}</code> {Encode(call.Arguments)}{outcome}</li>");
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
