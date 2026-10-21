using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Core.Composition;

/// <summary>Il testo per il cliente, in semplice e in HTML per l'editor di Zammad, sempre con la dicitura AI in coda.</summary>
public sealed record ComposedDraft(DraftKind Kind, string Text, string Html, IReadOnlyList<string> Questions);

/// <summary>
/// Composizione della bozza (P8, PLAN §5 Fase 6). La dicitura AI la aggiunge il codice, mai il modello: è uguale per risposta,
/// chiarimenti ed escalation, il testo del modello non la contiene e non la può togliere. Chiarimenti ed escalation hanno un
/// testo fisso: sotto soglia il codice non pubblica le soluzioni del modello.
/// </summary>
public static partial class DraftComposer
{
    /// <summary>
    /// Testo della dicitura (P8), confermato al CHECKPOINT 6 (2026-10-19): unico per chi la riceve, la distinzione "in tutto" /
    /// "in parte" sta solo nei tag della Fase 7. Resta da validare con chi segue la compliance.
    /// </summary>
    public const string AiDisclosure = "Risposta predisposta con il supporto di un sistema di intelligenza artificiale e verificata da un operatore.";

    /// <summary>Domande generiche quando né il modello né la classificazione ne propongono.</summary>
    internal static readonly IReadOnlyList<string> DefaultQuestions =
    [
        "Quale versione del prodotto utilizza e in quale modalità (installato sul Suo server oppure online)?",
        "Compare un codice di errore? Se sì, quale?",
        "Da quando si presenta il problema e riguarda tutti gli utenti o solo alcuni?"
    ];

    public static ComposedDraft Compose(DraftKind kind, string modelAnswer, IReadOnlyList<string> modelQuestions, IReadOnlyList<string> missingInformation, string contactFirstName)
    {
        // In una risposta valgono solo le domande del modello: quelle di ripiego servono a un chiarimento, non a una soluzione.
        List<string> questions = Clean(kind == DraftKind.Answer || modelQuestions.Count > 0 ? modelQuestions
            : missingInformation.Count > 0 ? missingInformation.Select(AsQuestion) : DefaultQuestions);

        string body = kind switch
        {
            DraftKind.Answer => Answer(modelAnswer, questions),
            DraftKind.Clarification => Clarification(contactFirstName, questions),
            DraftKind.Escalation => Escalation(contactFirstName),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        string text = $"{body}\n\n{AiDisclosure}";

        return new ComposedDraft(kind, text, $"{ToHtml(body)}<p><em>{WebUtility.HtmlEncode(AiDisclosure)}</em></p>", questions);
    }

    private static List<string> Clean(IEnumerable<string> questions) =>
        [.. questions.Select(q => q.Trim()).Where(q => q.Length > 0).Distinct().Take(4)];

    /// <summary>
    /// Le domande del modello in coda alla risposta, prima dei saluti: il testo può rimandare alle "domande seguenti" (Fase 8,
    /// HT-2026-0070), che altrimenti mancherebbero.
    /// </summary>
    internal static string Answer(string modelAnswer, IReadOnlyList<string> questions)
    {
        string answer = modelAnswer.Trim();

        if (questions.Count == 0)
        {
            return answer;
        }

        Match closing = Closing().Match(answer);
        string list = $"Per completare la verifica Le chiediamo di indicarci:\n{string.Join("\n", questions.Select((q, i) => $"{i + 1}. {q}"))}";

        return closing.Success
            ? $"{answer[..closing.Index].TrimEnd()}\n\n{list}\n\n{closing.Value.Trim()}"
            : $"{answer}\n\n{list}";
    }

    /// <summary>La bozza contiene la dicitura AI (test P8: nessuna bozza pubblicata senza).</summary>
    public static bool HasDisclosure(string textOrHtml) => textOrHtml.Contains(AiDisclosure, StringComparison.Ordinal);

    internal static string Clarification(string firstName, IReadOnlyList<string> questions) =>
        $"""
        Gentile {firstName},

        grazie per la segnalazione. Per poterla aiutare ci servono alcune informazioni:
        {string.Join("\n", questions.Select((q, i) => $"{i + 1}. {q}"))}

        Appena ci risponde procediamo con l'analisi.

        Cordiali saluti,
        """;

    internal static string Escalation(string firstName) =>
        $"""
        Gentile {firstName},

        grazie per la segnalazione. Il caso richiede un approfondimento da parte del nostro team specialistico: lo abbiamo preso in carico e la ricontatteremo appena possibile con un aggiornamento.

        Cordiali saluti,
        """;

    /// <summary>Un'informazione mancante scritta come frase ("versione del prodotto") diventa una domanda.</summary>
    internal static string AsQuestion(string missing)
    {
        string text = missing.Trim().TrimEnd('.');

        return text.EndsWith('?') ? text : $"Ci può indicare {char.ToLowerInvariant(text[0])}{text[1..]}?";
    }

    /// <summary>
    /// Testo semplice → HTML dell'editor: paragrafi sulle righe vuote, righe <c>1.</c> in un <c>&lt;ol&gt;</c>, righe <c>-</c> in
    /// un <c>&lt;ul&gt;</c>, <c>**grassetto**</c>; tutto il resto con l'escape.
    /// </summary>
    public static string ToHtml(string text)
    {
        var html = new StringBuilder();

        foreach (string paragraph in Blank().Split(text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim()))
        {
            List<string> plain = [];
            string? list = null;

            void FlushPlain()
            {
                if (plain.Count > 0)
                {
                    html.Append("<p>").Append(string.Join("<br>", plain)).Append("</p>");
                    plain.Clear();
                }
            }

            void CloseList()
            {
                if (list is not null)
                {
                    html.Append($"</{list}>");
                    list = null;
                }
            }

            foreach (string raw in paragraph.Split('\n'))
            {
                string line = raw.Trim();
                Match numbered = NumberedItem().Match(line);
                Match bullet = BulletItem().Match(line);
                string? kind = numbered.Success ? "ol" : bullet.Success ? "ul" : null;

                if (kind is null)
                {
                    CloseList();
                    plain.Add(Inline(line));
                    continue;
                }

                FlushPlain();

                if (list != kind)
                {
                    CloseList();
                    html.Append($"<{kind}>");
                    list = kind;
                }

                html.Append("<li>").Append(Inline((numbered.Success ? numbered : bullet).Groups[1].Value)).Append("</li>");
            }

            CloseList();
            FlushPlain();
        }

        return html.ToString();
    }

    private static string Inline(string text) => Bold().Replace(WebUtility.HtmlEncode(text), "<strong>$1</strong>");

    [GeneratedRegex(@"\n\s*Cordiali saluti,?\s*$")]
    private static partial Regex Closing();

    [GeneratedRegex(@"\n[ \t]*\n+")]
    private static partial Regex Blank();

    [GeneratedRegex(@"^\d+[.)]\s+(.+)$")]
    private static partial Regex NumberedItem();

    [GeneratedRegex(@"^[-•*]\s+(.+)$")]
    private static partial Regex BulletItem();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();
}
