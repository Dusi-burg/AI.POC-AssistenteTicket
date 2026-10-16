using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dusiburg.AI.Ticket.Rag.Retrieval;

/// <summary>
/// Tokenizzazione per il BM25 (fase-5.md §5): minuscole senza accenti, stopword italiane, troncamento leggero dei suffissi
/// flessivi (fattura/fatture, bloccato/bloccata). I token con cifre (codici errore, versioni) restano interi: <c>FF-E2041</c> dà
/// <c>ff-e2041</c> e anche le parti <c>ff</c>, <c>e2041</c>, così si trova anche chi scrive solo "E2041".
/// </summary>
public static partial class ItalianTokenizer
{
    private const int MinStemLength = 4;

    private static readonly HashSet<string> StopWords =
    [
        "a", "ad", "al", "allo", "ai", "agli", "all", "alla", "alle", "anche", "avere", "abbiamo", "avete", "hanno", "ho", "hai", "ha",
        "che", "chi", "ci", "come", "con", "col", "coi", "cosa", "da", "dal", "dallo", "dai", "dagli", "dall", "dalla", "dalle",
        "del", "dello", "dei", "degli", "dell", "della", "delle", "di", "dopo", "e", "ed", "era", "essere", "fa", "fare", "fino",
        "gli", "il", "in", "io", "la", "le", "lei", "li", "lo", "loro", "lui", "ma", "mi", "mio", "mia", "ne", "nei", "negli",
        "nel", "nello", "nell", "nella", "nelle", "noi", "non", "nostro", "nostra", "nostri", "nostre", "o", "od", "per", "perche",
        "piu", "poi", "quale", "quali", "quando", "quanto", "quello", "quella", "questo", "questa", "questi", "queste", "se", "si",
        "sia", "siamo", "siete", "sono", "su", "sul", "sullo", "sui", "sugli", "sull", "sulla", "sulle", "suo", "sua", "suoi", "tra",
        "fra", "tu", "un", "una", "uno", "vi", "voi", "vostro", "vostra", "gia", "ancora", "sempre", "solo", "molto", "tutto",
        "tutti", "tutte", "ogni", "oppure", "dove", "cui", "stato", "stata", "stati", "viene", "vengono", "c", "l", "d", "un'",
        "buongiorno", "grazie", "salve", "cordiali", "saluti", "gentile"
    ];

    public static IReadOnlyList<string> Tokenize(string text)
    {
        List<string> tokens = [];

        foreach (Match match in Word().Matches(RemoveDiacritics(text.ToLowerInvariant())))
        {
            string word = match.Value;

            if (word.Any(char.IsDigit))
            {
                tokens.Add(word);

                if (word.Contains('-', StringComparison.Ordinal))
                {
                    tokens.AddRange(word.Split('-', StringSplitOptions.RemoveEmptyEntries).Where(part => part.Length > 1));
                }

                continue;
            }

            foreach (string part in word.Split(['-', '.', '_'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Length > 1 && !StopWords.Contains(part))
                {
                    tokens.Add(Stem(part));
                }
            }
        }

        return tokens;
    }

    /// <summary>Suffissi più frequenti dell'italiano tecnico, poi la vocale finale: niente stemmer completo, basta che le forme flesse coincidano.</summary>
    internal static string Stem(string word)
    {
        foreach (string suffix in (string[])["azioni", "azione", "amento", "amenti", "mente", "zioni", "zione"])
        {
            if (word.Length - suffix.Length >= MinStemLength && word.EndsWith(suffix, StringComparison.Ordinal))
            {
                return word[..^suffix.Length];
            }
        }

        foreach (string suffix in (string[])["ata", "ate", "ati", "ato", "ita", "ite", "iti", "ito"])
        {
            if (word.Length - suffix.Length >= MinStemLength && word.EndsWith(suffix, StringComparison.Ordinal))
            {
                return word[..^suffix.Length];
            }
        }

        return word.Length > MinStemLength && "aeiou".Contains(word[^1], StringComparison.Ordinal) ? word[..^1] : word;
    }

    private static string RemoveDiacritics(string text)
    {
        string decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (char c in decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
        {
            builder.Append(c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Lettere e cifre, con <c>-</c>, <c>.</c> o <c>_</c> interni (<c>ff-e2041</c>, <c>5.1.2</c>, <c>ff_admin</c>); l'apostrofo separa.</summary>
    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[-._][\p{L}\p{N}]+)*")]
    private static partial Regex Word();
}
