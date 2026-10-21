using System.Globalization;
using System.Text;
using Dusiburg.AI.Ticket.Core.Composition;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Core.Evaluation;

/// <summary>
/// Report Markdown di <c>eval drafts</c> (fase-8.md §2): parametri, sintesi con le soglie del PLAN §1, decisioni, effetto della
/// verifica a valle, tempi, una riga per caso e i testi completi per la griglia manuale.
/// </summary>
public static class DraftReportRenderer
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public static string Render(DraftEvaluationRun run, IReadOnlyList<DraftGrade>? grades, string runFile, string? gradeFile)
    {
        DraftEvaluationSummary s = DraftEvaluationSummary.Of(run, grades);
        Dictionary<string, DraftGrade> gradeOf = (grades ?? []).ToDictionary(g => g.TicketNumber, StringComparer.Ordinal);
        var md = new StringBuilder();

        md.AppendLine($"# Valutazione delle bozze — {run.RunAt.ToString("yyyy-MM-dd HH:mm", Italian)}");
        md.AppendLine();
        md.AppendLine("> Generato da `dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- eval drafts` (fase 8): la pipeline dell'Agent a secco sui");
        md.AppendLine("> ticket del test set, con il contesto preso dal database. Le letture sono in `" + runFile + "`: con `--replay` il report si");
        md.AppendLine("> ricalcola senza richiamare il modello, con `--griglia` aggiunge i giudizi della griglia manuale.");
        md.AppendLine();

        AppendParameters(md, run, s, gradeFile);
        AppendSummary(md, s);
        AppendDecisions(md, run);
        AppendWithoutVerification(md, s);
        AppendTimings(md, s, run.Mode);
        AppendAgent(md, run);
        AppendCases(md, run, gradeOf);
        AppendTexts(md, run, gradeOf);

        return md.ToString();
    }

    private static void AppendParameters(StringBuilder md, DraftEvaluationRun run, DraftEvaluationSummary s, string? gradeFile)
    {
        int traps = run.Cases.Count(c => c.Trap is not null);

        md.AppendLine("| Parametro | Valore |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| Modelli | `{run.ClassifyModel}` (classificazione), `{run.DraftModel}` ({(run.Mode == PipelineMode.Agent ? "agente con i tool" : "bozza")}) — provider `{run.Provider}` |");
        md.AppendLine($"| Modalità | {(run.Mode == PipelineMode.Agent ? "**agente**: le fonti le sceglie il modello con i tool di `ticket-mcp` (Fase 9b)" : "pipeline fissa: le fonti le sceglie il retrieval")} |");
        md.AppendLine($"| Casi | {run.Cases.Count}: {run.Cases.Count - traps} normali (stratificati per prodotto e modalità), {traps} trabocchetti |");
        md.AppendLine($"| Soglia della confidenza | {Number(run.ConfidenceMinForDraft)} |");
        md.AppendLine($"| Durata | {Seconds(run.Duration.TotalMilliseconds)} in tutto, {Seconds(run.Duration.TotalMilliseconds / Math.Max(1, run.Cases.Count))} a caso |");
        md.AppendLine($"| Token (ingresso / uscita) | {(s.InputTokens is { } i && s.OutputTokens is { } o ? $"{i.ToString("N0", Italian)} / {o.ToString("N0", Italian)}" : "non restituiti dal provider")} |");
        md.AppendLine($"| Costo | {Usd(s.Cost)} in tutto, {Usd(s.Cost is { } c ? c / Math.Max(1, run.Cases.Count) : null)} a caso{(run.Provider == Ai.ModelProviders.Anthropic ? $" (listino al {ModelPrices.AsOf})" : " (in locale)")} |");

        if (run.LoadedModels.Count > 0)
        {
            md.AppendLine($"| Modelli caricati in Ollama | {string.Join("; ", run.LoadedModels)} |");
        }

        md.AppendLine($"| Griglia manuale | {(gradeFile is null ? "non applicata" : $"`{gradeFile}`, {s.Graded} giudizi su {run.Cases.Count}")} |");
        md.AppendLine();
    }

    private static void AppendSummary(StringBuilder md, DraftEvaluationSummary s)
    {
        md.AppendLine("## Sintesi");
        md.AppendLine();
        md.AppendLine("| Misura | Valore | Soglia (PLAN §1) |");
        md.AppendLine("|---|---|---|");
        md.AppendLine(s.UsefulShare is { } useful
            ? $"| **Bozze utili** (corrette o utili con modifiche, griglia) | **{s.Useful} su {s.Graded} ({Percent(useful)})** | ≥ {Percent(DraftEvaluationSummary.MinUsefulShare)}: {Verdict(useful >= DraftEvaluationSummary.MinUsefulShare)} |"
            : "| Bozze utili (griglia) | griglia non applicata | ≥ 60% |");
        md.AppendLine(s.PublishedUnsupportedShare is { } unsupported
            ? $"| **Termini tecnici non supportati** nelle bozze proposte | **{s.PublishedUnsupportedTerms} su {s.PublishedTechnicalTerms} ({Percent(unsupported)})** | < {Percent(DraftEvaluationSummary.MaxUnsupportedShare)}: {Verdict(unsupported < DraftEvaluationSummary.MaxUnsupportedShare)} |"
            : "| Termini tecnici non supportati nelle bozze proposte | nessun termine tecnico | < 5% |");

        if (s.Graded > 0)
        {
            md.AppendLine($"| Affermazioni non supportate segnate nella griglia | {s.ManualUnsupportedClaims} | |");
        }

        md.AppendLine($"| Trabocchetti con l'atteso rispettato | {s.TrapsMet} su {s.TrapsWithExpectation} | |");
        md.AppendLine($"| Risposte che citano una fonte del caso | {s.AnswersCitingRelevant} su {s.Answers} (con una fonte del caso nel prompt: {s.AnswersWithRelevantInPrompt}) | |");
        md.AppendLine($"| Tempo della pipeline (senza la pubblicazione) | media {Seconds(s.Total.Mean)}, p95 {Seconds(s.Total.P95)} | < 45 s |");
        md.AppendLine($"| Casi senza bozza (errori) | {s.Failed} | |");
        md.AppendLine();
    }

    private static void AppendDecisions(StringBuilder md, DraftEvaluationRun run)
    {
        md.AppendLine("## Decisioni del codice");
        md.AppendLine();
        md.AppendLine("| Casi | Risposta | Chiarimenti | Escalation | Errore | Atteso automatico | Rispettato |");
        md.AppendLine("|---|---|---|---|---|---|---|");

        foreach (IGrouping<TrapKind?, DraftCaseRecord> group in run.Cases.GroupBy(c => c.Trap).OrderBy(g => g.Key is null ? -1 : (int)g.Key.Value))
        {
            List<DraftCaseRecord> cases = [.. group];
            string? expected = DraftExpectations.Describe(group.Key);

            md.AppendLine($"| {(group.Key is { } trap ? $"trabocchetto {trap}" : "normali")} ({cases.Count}) " +
                $"| {cases.Count(c => c.Kind == DraftKind.Answer)} | {cases.Count(c => c.Kind == DraftKind.Clarification)} " +
                $"| {cases.Count(c => c.Kind == DraftKind.Escalation)} | {cases.Count(c => !c.Succeeded)} | {expected ?? "solo griglia"} " +
                $"| {(expected is null ? "—" : $"{cases.Count(c => DraftExpectations.IsMet(c) == true)} su {cases.Count}")} |");
        }

        md.AppendLine();
    }

    private static void AppendWithoutVerification(StringBuilder md, DraftEvaluationSummary s)
    {
        md.AppendLine("## Con e senza la verifica a valle");
        md.AppendLine();
        md.AppendLine("Che cosa sarebbe arrivato nell'editor dell'operatore pubblicando la risposta del modello così com'è, senza la verifica e la");
        md.AppendLine("decisione del codice (Fase 6).");
        md.AppendLine();
        md.AppendLine("| Senza verifica | Casi |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| Citazioni di fonti non fornite | {s.InvalidCitations} |");
        md.AppendLine($"| Risposte del modello con termini tecnici assenti da fonti e ticket | {s.ModelAnswersWithUnsupportedTerms} ({s.ModelUnsupportedTerms} termini su {s.ModelTechnicalTerms}{(s.ModelUnsupportedShare is { } share ? $", {Percent(share)}" : "")}) |");
        md.AppendLine($"| Firme aggiunte dal modello (tolte dal codice) | {s.SignaturesRemoved} |");
        md.AppendLine($"| Risposte proposte dal modello ma non pubblicate (confidenza sotto soglia) | {s.AnswersDeclinedByCode} |");
        md.AppendLine();
    }

    private static void AppendTimings(StringBuilder md, DraftEvaluationSummary s, PipelineMode mode)
    {
        md.AppendLine("## Tempi per passo");
        md.AppendLine();
        md.AppendLine("| Passo | Media | p95 |");
        md.AppendLine("|---|---|---|");
        md.AppendLine($"| Classificazione | {Seconds(s.Classification.Mean)} | {Seconds(s.Classification.P95)} |");
        md.AppendLine($"| Retrieval (con l'embedding della query) | {Seconds(s.Retrieval.Mean)} | {Seconds(s.Retrieval.P95)} |");
        md.AppendLine($"| {(mode == PipelineMode.Agent ? "Agente (tool e bozza; il retrieval è dentro i tool)" : "Generazione")} | {Seconds(s.Generation.Mean)} | {Seconds(s.Generation.P95)} |");
        md.AppendLine($"| **Totale** | **{Seconds(s.Total.Mean)}** | **{Seconds(s.Total.P95)}** |");
        md.AppendLine();
    }

    /// <summary>Modalità agente: quante chiamate, quali tool, quante fermate dalla guardia, se la fonte del caso è stata letta.</summary>
    private static void AppendAgent(StringBuilder md, DraftEvaluationRun run)
    {
        List<DraftCaseRecord> traced = [.. run.Cases.Where(c => c.Agent is not null)];

        if (run.Mode != PipelineMode.Agent || traced.Count == 0)
        {
            return;
        }

        List<int> calls = [.. traced.Select(c => c.Agent!.Calls.Count)];
        List<DraftCaseRecord> withRelevant = [.. traced.Where(c => c.CaseCode is not null && c.Trap is null)];

        md.AppendLine("## Modalità agente");
        md.AppendLine();
        md.AppendLine("| Misura | Valore |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| Chiamate ai tool per caso | media {calls.Average().ToString("0.0", Italian)}, massimo {calls.Max()} |");
        md.AppendLine($"| Casi **senza** chiamate | {calls.Count(c => c == 0)} su {traced.Count} |");
        md.AppendLine($"| Chiamate fermate dalla guardia / in errore | {traced.Sum(c => c.Agent!.Rejected)} / {traced.Sum(c => c.Agent!.Errors)} |");
        md.AppendLine($"| Fonti lette per caso | media {traced.Average(c => c.PromptSources.Count).ToString("0.0", Italian)} |");
        md.AppendLine($"| Casi normali con una fonte del caso fra le lette | {withRelevant.Count(c => c.RelevantPromptSources.Count > 0)} su {withRelevant.Count} |");
        md.AppendLine();
        md.AppendLine("| Tool | Chiamate | Riuscite |");
        md.AppendLine("|---|---|---|");

        foreach (IGrouping<string, ToolCallTrace> tool in traced.SelectMany(c => c.Agent!.Calls).GroupBy(c => c.Tool).OrderByDescending(g => g.Count()))
        {
            md.AppendLine($"| `{tool.Key}` | {tool.Count()} | {tool.Count(c => c.Outcome == "ok")} |");
        }

        md.AppendLine();
    }

    private static void AppendCases(StringBuilder md, DraftEvaluationRun run, IReadOnlyDictionary<string, DraftGrade> gradeOf)
    {
        md.AppendLine("## Casi");
        md.AppendLine();
        md.AppendLine("| Ticket | Caso | Contesto | Decisione | Confidenza | Citazioni (valide / del caso) | Termini non supportati | Atteso | Giudizio |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");

        foreach (DraftCaseRecord c in run.Cases)
        {
            string decision = c.Succeeded ? InternalNoteComposer.KindLabel(c.Kind!.Value) : $"errore: {Cell(c.Error!)}";
            string citations = c.Citations.Count == 0 ? "—" : $"{c.Citations.Count} ({c.Citations.Count(x => x.IsVerified)} / {c.Citations.Count(x => x.IsRelevant)})";
            string expected = DraftExpectations.IsMet(c) switch { true => "✔", false => "✘", null => "—" };

            md.AppendLine($"| [{c.TicketNumber}](#{Anchor(c.TicketNumber)}) | {(c.Trap is { } trap ? $"**{trap}** " : "")}{c.CaseCode ?? "—"} " +
                $"| {c.ProductCode} {c.Version ?? "?"} {c.Mode} | {decision} | {(c.Confidence is { } confidence ? Number(confidence) : "—")} | {citations} " +
                $"| {(c.UnsupportedTerms.Count == 0 ? "—" : Cell(string.Join(", ", c.UnsupportedTerms)))} | {expected} | {Judgment(gradeOf.GetValueOrDefault(c.TicketNumber))} |");
        }

        md.AppendLine();
    }

    private static void AppendTexts(StringBuilder md, DraftEvaluationRun run, IReadOnlyDictionary<string, DraftGrade> gradeOf)
    {
        md.AppendLine("## Testi");
        md.AppendLine();
        md.AppendLine("Per ogni caso: il messaggio del cliente, la bozza che l'operatore avrebbe trovato nell'editor (dicitura AI compresa), le fonti");
        md.AppendLine("del prompt (in grassetto quelle dello stesso caso) e il giudizio della griglia.");

        foreach (DraftCaseRecord c in run.Cases)
        {
            md.AppendLine();
            md.AppendLine($"### {c.TicketNumber}");
            md.AppendLine();
            md.AppendLine($"**{Cell(c.Title)}** — {c.ProductCode} {c.Version ?? "versione ignota"} {c.Mode}, caso `{c.CaseCode ?? "—"}`{(c.Trap is { } trap ? $", trabocchetto **{trap}**" : "")}");
            md.AppendLine();
            md.AppendLine(Quote(c.CustomerMessage));
            md.AppendLine();

            if (!c.Succeeded)
            {
                md.AppendLine($"Nessuna bozza: {c.Error}");
                continue;
            }

            md.AppendLine($"Classificazione: {c.Category}, priorità {c.Priority}{(c.SuspectedManipulation ? ", **manipolazione segnalata**" : "")}" +
                $"{(c.MissingInformation.Count == 0 ? "" : $"; mancano: {string.Join("; ", c.MissingInformation)}")}. " +
                $"Decisione: **{InternalNoteComposer.KindLabel(c.Kind!.Value)}** ({c.Reason}).");
            md.AppendLine();
            md.AppendLine("```text");
            md.AppendLine(c.DraftText);
            md.AppendLine("```");
            md.AppendLine();
            md.AppendLine($"{(c.Agent is null ? "Fonti del prompt" : "Fonti lette dall'agente")}: {(c.PromptSources.Count == 0 ? "nessuna" : string.Join(", ", c.PromptSources.Select(k => c.RelevantPromptSources.Contains(k) ? $"**{k}**" : k)))}." +
                $" Citate: {(c.Citations.Count == 0 ? "nessuna" : string.Join(", ", c.Citations.Select(x => x.IsVerified ? x.Id : $"{x.Id} (non fornita)")))}.");

            if (c.Agent is { } agent)
            {
                md.AppendLine();
                md.AppendLine(agent.Calls.Count == 0
                    ? "Chiamate ai tool: nessuna."
                    : $"Chiamate ai tool: {string.Join(" → ", agent.Calls.Select(call => $"`{call.Tool}` {Cell(call.Arguments)}{(call.Outcome == "ok" ? "" : $" ({call.Outcome})")}"))}.");
            }

            if (c.Kind != DraftKind.Answer && c.ModelProposedAnswer)
            {
                md.AppendLine();
                md.AppendLine("Risposta del modello non pubblicata (sotto soglia):");
                md.AppendLine();
                md.AppendLine(Quote(c.ModelAnswer));
            }

            if (gradeOf.GetValueOrDefault(c.TicketNumber) is { Judgment: not null } grade)
            {
                md.AppendLine();
                md.AppendLine($"Giudizio: **{Judgment(grade)}**{(grade.UnsupportedClaims is > 0 ? $", {grade.UnsupportedClaims} affermazioni non supportate" : "")}" +
                    $"{(grade.Note is { Length: > 0 } note ? $" — {note}" : "")}{(grade.Author is { Length: > 0 } author ? $" ({author})" : "")}");
            }
        }
    }

    private static string Judgment(DraftGrade? grade) => grade?.Judgment switch
    {
        DraftJudgment.Correct => "corretta",
        DraftJudgment.UsefulWithEdits => "utile con modifiche",
        DraftJudgment.Discard => "da scartare",
        _ => "—"
    };

    private static string Verdict(bool met) => met ? "raggiunta" : "**non raggiunta**";

    private static string Quote(string text) => string.Join("\n", text.Trim().Split('\n').Select(line => $"> {line.TrimEnd()}"));

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Anchor(string ticketNumber) => ticketNumber.ToLowerInvariant();

    private static string Number(double value) => value.ToString("0.00", Italian);

    private static string Percent(double value) => value.ToString("0.0%", Italian);

    private static string Seconds(double milliseconds) => (milliseconds / 1000).ToString("0.0", Italian) + " s";

    private static string Usd(decimal? value) => value is { } v ? v.ToString("0.0000", Italian) + " $" : "—";
}
