using System.Text.RegularExpressions;
using Dusiburg.AI.Ticket.Core.Composition;
using Dusiburg.AI.Ticket.Core.Context;

namespace Dusiburg.AI.Ticket.Core.Feedback;

/// <summary>
/// Distanza di modifica fra la bozza e il testo inviato (fase-7.md §1): Levenshtein a parole, normalizzata sul più lungo dei due
/// testi (0 = invariato, 1 = tutto diverso). A parole e non a caratteri: si legge come "quota delle parole cambiate" e non risente
/// della formattazione. I due testi passano dalla stessa conversione HTML → testo e perdono la dicitura AI, che si valuta a parte.
/// </summary>
public static partial class EditDistance
{
    /// <summary>Testo confrontabile della bozza registrata (testo semplice con la dicitura in coda, come in <c>AgentDraft.Body</c>).</summary>
    public static string ComparableDraft(string draftText) =>
        Comparable(HtmlText.ToPlainText(DraftComposer.ToHtml(WithoutDisclosure(draftText)), "text/html"));

    /// <summary>Testo confrontabile di un articolo di Zammad (HTML o testo semplice).</summary>
    public static string ComparableArticle(string body, string? contentType) =>
        Comparable(WithoutDisclosure(HtmlText.ToPlainText(body, contentType)));

    /// <summary>L'articolo contiene la dicitura AI, a meno di spazi e a capo.</summary>
    public static bool HasDisclosure(string body, string? contentType) =>
        Comparable(HtmlText.ToPlainText(body, contentType)).Contains(Comparable(DraftComposer.AiDisclosure), StringComparison.Ordinal);

    public static double Words(string comparableDraft, string comparableSent)
    {
        string[] draft = Tokens(comparableDraft);
        string[] sent = Tokens(comparableSent);

        if (draft.Length == 0 && sent.Length == 0)
        {
            return 0;
        }

        return Math.Round((double)Levenshtein(draft, sent) / Math.Max(draft.Length, sent.Length), 3);
    }

    /// <summary>Inserimenti, cancellazioni e sostituzioni di parole, con due righe della matrice.</summary>
    internal static int Levenshtein(string[] source, string[] target)
    {
        int[] previous = [.. Enumerable.Range(0, target.Length + 1)];
        int[] current = new int[target.Length + 1];

        for (int i = 1; i <= source.Length; i++)
        {
            current[0] = i;

            for (int j = 1; j <= target.Length; j++)
            {
                int substitution = previous[j - 1] + (string.Equals(source[i - 1], target[j - 1], StringComparison.Ordinal) ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[target.Length];
    }

    private static string WithoutDisclosure(string text) => text.Replace(DraftComposer.AiDisclosure, string.Empty, StringComparison.Ordinal);

    private static string Comparable(string text) => Spaces().Replace(text, " ").Trim();

    private static string[] Tokens(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
