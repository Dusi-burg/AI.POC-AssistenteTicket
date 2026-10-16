using System.Text.RegularExpressions;

namespace Dusiburg.AI.Ticket.Rag.Cleaning;

/// <summary>Nomi del contesto da sostituire: il referente che scrive e il cliente (ragione sociale).</summary>
public sealed record KnownNames(IReadOnlyList<string> People, IReadOnlyList<string> Organizations)
{
    public static KnownNames None { get; } = new([], []);

    public static KnownNames Of(string? person, string? organization) =>
        new([.. NonEmpty(person)], [.. NonEmpty(organization)]);

    private static IEnumerable<string> NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [value.Trim()];
}

/// <summary>
/// Sostituisce i dati personali con segnaposto fra parentesi quadre (fase-5.md §2): niente email, telefoni, partite IVA, codici
/// fiscali, IBAN, token e siti nell'indice né nel prompt. L'ordine conta: email e IBAN prima delle sequenze di cifre, token prima
/// delle parole. Codici errore (<c>FF-E2041</c>), versioni (<c>5.1.2</c>) e numeri brevi restano.
/// </summary>
public static partial class Anonymizer
{
    public const string Email = "[EMAIL]";
    public const string Site = "[SITO]";
    public const string Iban = "[IBAN]";
    public const string Token = "[TOKEN]";
    public const string TaxCode = "[CF]";
    public const string VatNumber = "[PIVA]";
    public const string Phone = "[TELEFONO]";
    public const string Person = "[NOME]";
    public const string Organization = "[CLIENTE]";

    public static string Anonymize(string text, KnownNames names)
    {
        string result = EmailPattern().Replace(text, Email);
        result = SitePattern().Replace(result, Site);
        result = IbanPattern().Replace(result, Iban);
        result = ApiTokenPattern().Replace(result, Token);
        result = LongTokenPattern().Replace(result, Token);
        result = TaxCodePattern().Replace(result, TaxCode);
        result = VatNumberPattern().Replace(result, VatNumber);
        result = PhonePattern().Replace(result, Phone);

        foreach (string organization in names.Organizations)
        {
            result = ReplaceWords(result, organization, Organization, RegexOptions.IgnoreCase);
        }

        foreach (string person in names.People)
        {
            result = ReplaceWords(result, person, Person, RegexOptions.IgnoreCase);

            // Anche il solo nome o cognome ("Gentile Anna", "Sig. Ferretti"), ma con l'iniziale maiuscola: "Sala" è un nome,
            // "sala server" no.
            foreach (string part in person.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(part => part.Length >= 3))
            {
                result = ReplaceWords(result, part, Person, RegexOptions.None);
            }
        }

        return result;
    }

    private static string ReplaceWords(string text, string words, string placeholder, RegexOptions caseOption) =>
        Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(words)}(?![\p{{L}}\p{{N}}])", placeholder,
            caseOption | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b(?:https?://)?www\.[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+(?:/\S*)?", RegexOptions.IgnoreCase)]
    private static partial Regex SitePattern();

    /// <summary>IBAN italiano (27 caratteri), anche a gruppi di quattro separati da spazi.</summary>
    [GeneratedRegex(@"\bIT\d{2}\s?[A-Z](?:\s?[0-9A-Z]){22}\b", RegexOptions.IgnoreCase)]
    private static partial Regex IbanPattern();

    /// <summary>Chiavi delle integrazioni (<c>ff_live_…</c>, <c>sk-…</c>).</summary>
    [GeneratedRegex(@"\b[a-z]{2,6}_(?:live|test)_[A-Za-z0-9]{12,}\b|\bsk-[A-Za-z0-9_-]{16,}", RegexOptions.IgnoreCase)]
    private static partial Regex ApiTokenPattern();

    /// <summary>Sequenze lunghe di lettere e cifre mescolate: token, hash, chiavi incollate per errore.</summary>
    [GeneratedRegex(@"\b(?=[A-Za-z0-9]*\d)(?=[A-Za-z0-9]*[A-Za-z])[A-Za-z0-9]{24,}\b")]
    private static partial Regex LongTokenPattern();

    /// <summary>Codice fiscale delle persone fisiche: 6 lettere, 2 cifre, mese, 2 cifre, comune, 3 cifre, controllo.</summary>
    [GeneratedRegex(@"\b[A-Z]{6}\d{2}[ABCDEHLMPRST]\d{2}[A-Z]\d{3}[A-Z]\b", RegexOptions.IgnoreCase)]
    private static partial Regex TaxCodePattern();

    /// <summary>Partita IVA (e codice fiscale delle società): 11 cifre, eventualmente con il prefisso IT.</summary>
    [GeneratedRegex(@"\b(?:IT)?\d{11}\b")]
    private static partial Regex VatNumberPattern();

    /// <summary>Fissi (prefisso 0, 6–11 cifre in tutto) e cellulari (3xx + 6–7 cifre), con +39 e separatori facoltativi.</summary>
    [GeneratedRegex(@"(?<![\w.,/-])(?:\+39[\s.]?)?(?:0\d{1,3}[\s.-]?\d{4,8}|3\d{2}[\s.-]?\d{6,7})(?![\w.,/-]?\d)")]
    private static partial Regex PhonePattern();
}
