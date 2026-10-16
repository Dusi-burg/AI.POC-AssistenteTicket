using System.Text.RegularExpressions;

namespace Dusiburg.AI.Ticket.Rag.Cleaning;

/// <summary>
/// Toglie da un messaggio del cliente ciò che non descrive il problema (fase-5.md §2): citazioni del messaggio precedente,
/// disclaimer, firma, saluti di apertura e frasi di cortesia. Lavora per paragrafi (blocchi separati da una riga vuota): il primo
/// è sempre contenuto; dal secondo in poi il primo paragrafo che sembra una firma chiude il messaggio. Si applica prima
/// dell'anonimizzazione, perché il nome del referente aiuta a riconoscere la firma.
/// </summary>
public static partial class MessageCleaner
{
    /// <summary>Righe brevi oltre cui una riga non è più un saluto o una frase di cortesia.</summary>
    private const int CourtesyMaxWords = 12;

    /// <summary>Un paragrafo di righe tutte così brevi, dopo il primo, è una firma (ruolo, azienda, recapiti).</summary>
    private const int SignatureLineMaxWords = 5;

    public static string Clean(string body, IReadOnlyCollection<string> knownNames)
    {
        string text = CutQuote(body.Replace("\r\n", "\n", StringComparison.Ordinal));
        List<List<string>> paragraphs = Paragraphs(text);
        List<string> kept = [];

        for (int index = 0; index < paragraphs.Count; index++)
        {
            List<string> lines = paragraphs[index];

            if (IsDisclaimer(lines))
            {
                continue;
            }

            if (index > 0 && IsSignature(lines, knownNames))
            {
                break;
            }

            List<string> content = CutInlineSignature(lines, knownNames)
                .Where((line, position) => !(index == 0 && position == 0 && IsGreeting(line)) && !IsCourtesy(line))
                .ToList();

            if (content.Count > 0)
            {
                kept.Add(string.Join('\n', content));
            }

            if (content.Count < lines.Count && HasInlineSignature(lines, knownNames))
            {
                break;
            }
        }

        string cleaned = string.Join("\n\n", kept).Trim();

        // Un messaggio fatto solo di saluti (es. "Grazie, ora funziona!") resta com'è piuttosto che vuoto.
        return cleaned.Length > 0 ? cleaned : body.Trim();
    }

    /// <summary>Tutto ciò che segue l'intestazione di una citazione o la prima riga citata con <c>&gt;</c>.</summary>
    private static string CutQuote(string text)
    {
        Match quote = QuoteStart().Match(text);

        return quote.Success ? text[..quote.Index] : text;
    }

    private static List<List<string>> Paragraphs(string text) =>
        [.. ParagraphSeparator().Split(text)
            .Select(paragraph => paragraph.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList())
            .Where(lines => lines.Count > 0)];

    private static bool IsDisclaimer(List<string> lines) => lines.Any(line => Disclaimer().IsMatch(line));

    private static bool IsSignature(List<string> lines, IReadOnlyCollection<string> knownNames) =>
        lines[0] == "--"
        || lines.Any(line => IsNameLine(line, knownNames) || Contact().IsMatch(line) || line.StartsWith("Inviato da", StringComparison.Ordinal))
        || lines.All(line => Words(line) <= SignatureLineMaxWords && !line.Contains('?', StringComparison.Ordinal) && !IsSentence(line));

    private static bool HasInlineSignature(List<string> lines, IReadOnlyCollection<string> knownNames) =>
        lines.Skip(1).Any(line => line == "--" || IsNameLine(line, knownNames));

    /// <summary>Firma attaccata al testo senza riga vuota: si taglia dalla riga <c>--</c> o dal nome del referente.</summary>
    private static IEnumerable<string> CutInlineSignature(List<string> lines, IReadOnlyCollection<string> knownNames) =>
        lines.TakeWhile((line, position) => position == 0 || (line != "--" && !IsNameLine(line, knownNames)));

    /// <summary>
    /// La riga comincia con un nome completo noto ("Nome Cognome", "Nome Cognome - Ruolo") e non è una frase, oppure è il solo
    /// nome di battesimo (firma "{nome}").
    /// </summary>
    private static bool IsNameLine(string line, IReadOnlyCollection<string> knownNames) =>
        knownNames.Any(name => name.Length > 0
            && ((line.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                    && (line.Length == name.Length || !char.IsLetter(line[name.Length]))
                    && Words(line) <= SignatureLineMaxWords + 2)
                || line.TrimEnd('.', ',', '!').Equals(name.Split(' ')[0], StringComparison.OrdinalIgnoreCase)));

    private static bool IsGreeting(string line) => Words(line) <= 6 && Greeting().IsMatch(line);

    private static bool IsCourtesy(string line) => Words(line) <= CourtesyMaxWords && Courtesy().IsMatch(line);

    /// <summary>Una riga che termina come una frase (punto dopo una parola minuscola o una cifra), non come "S.r.l.".</summary>
    private static bool IsSentence(string line) => SentenceEnd().IsMatch(line);

    private static int Words(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    [GeneratedRegex(@"^(?:-{3,}\s*Messaggio originale|Il .{1,80} ha scritto:\s*$|>)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex QuoteStart();

    [GeneratedRegex(@"\n[ \t]*\n")]
    private static partial Regex ParagraphSeparator();

    [GeneratedRegex(@"riservat|confidenzial|2016/679|prima di stampare|destinatari indicati", RegexOptions.IgnoreCase)]
    private static partial Regex Disclaimer();

    /// <summary>Recapiti tipici di una firma: telefoni, partita IVA, codice fiscale, email, sito.</summary>
    [GeneratedRegex(@"^(?:Tel\.|Cell\.|P\.\s?IVA|C\.F\.|www\.)|@|^\+?[\d .]{8,}\s*(?:\||$)", RegexOptions.IgnoreCase)]
    private static partial Regex Contact();

    [GeneratedRegex(@"^(?:buongiorno|buonasera|salve|ciao|gentile|gentili|spett\.?le|egregi|alla cortese)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Greeting();

    [GeneratedRegex(
        @"^(?:grazie|cordiali saluti|distinti saluti|saluti|un saluto|buona giornata|buon lavoro|restiamo in attesa|resto in attesa|attendo|in attesa di|fatemi sapere|mi fate sapere|fateci sapere)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Courtesy();

    [GeneratedRegex(@"(?:[a-zà-ù\d\)""]{2,}|\d)[.!]$")]
    private static partial Regex SentenceEnd();
}
