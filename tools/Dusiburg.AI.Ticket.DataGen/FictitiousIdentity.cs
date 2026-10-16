using System.Globalization;
using System.Numerics;
using System.Text;

namespace Dusiburg.AI.Ticket.DataGen;

/// <summary>Referente di un cliente: i recapiti finiscono nelle firme e sono il materiale dell'anonimizzazione (Fase 5).</summary>
internal sealed record Contact(string FirstName, string LastName, string Role, string Phone, string Mobile, string Email, string TaxCode);

/// <summary>
/// Dati personali e fiscali inventati ma formalmente validi (cifre e caratteri di controllo corretti), così l'anonimizzazione si
/// prova su formati veri. Le partite IVA usano l'ufficio <c>000</c>, che non esiste: nessuna coincide con un'azienda reale.
/// </summary>
internal static class FictitiousIdentity
{
    private static readonly string[] AreaCodes = ["02", "06", "011", "051", "055", "081", "030", "035", "045", "049", "040", "0471"];
    private const string MonthLetters = "ABCDEHLMPRST";
    private const string TokenAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static Contact Contact(Random random, string firstName, string lastName, string role, string emailDomain) => new(
        firstName,
        lastName,
        role,
        Phone(random),
        Mobile(random),
        $"{Slug(firstName)}.{Slug(lastName)}@{emailDomain}",
        TaxCode(random, firstName, lastName));

    public static string Phone(Random random)
    {
        string area = random.Pick(AreaCodes);

        return $"{area} {random.Digits(10 - area.Length)}";
    }

    public static string Mobile(Random random) => $"3{random.Next(20, 100)} {random.Digits(7)}";

    /// <summary>Partita IVA di 11 cifre con l'ufficio fittizio <c>000</c> e la cifra di controllo corretta.</summary>
    public static string VatNumber(Random random)
    {
        string body = random.Digits(7) + "000";
        int sum = 0;

        for (int i = 0; i < body.Length; i++)
        {
            int digit = body[i] - '0';

            // Posizioni pari (1-based): cifra raddoppiata, meno 9 se supera 9.
            sum += i % 2 == 0 ? digit : digit * 2 > 9 ? digit * 2 - 9 : digit * 2;
        }

        return body + (char)('0' + (10 - sum % 10) % 10);
    }

    /// <summary>Codice fiscale nel formato corretto con il carattere di controllo; comune fittizio (codice <c>Z9xx</c>).</summary>
    public static string TaxCode(Random random, string firstName, string lastName)
    {
        int day = random.Next(1, 29) + (random.Chance(0.5) ? 40 : 0);
        string body = string.Create(CultureInfo.InvariantCulture,
            $"{Consonants(lastName, isFirstName: false)}{Consonants(firstName, isFirstName: true)}{random.Next(50, 100):D2}{MonthLetters[random.Next(12)]}{day:D2}Z9{random.Digits(2)}");

        return body + TaxCodeCheck(body);
    }

    /// <summary>IBAN italiano con le cifre di controllo corrette (mod 97); banca e conto inventati.</summary>
    public static string Iban(Random random)
    {
        string bban = $"{(char)('A' + random.Next(26))}{random.Digits(5)}{random.Digits(5)}{random.Digits(12)}";
        BigInteger number = BigInteger.Parse(ToIbanDigits(bban + "IT00"), CultureInfo.InvariantCulture);
        int check = 98 - (int)(number % 97);

        return $"IT{check:D2}{bban}";
    }

    public static string ApiToken(Random random, string productCode) =>
        $"{productCode.ToLowerInvariant()}_live_{string.Concat(Enumerable.Range(0, 32).Select(_ => TokenAlphabet[random.Next(TokenAlphabet.Length)]))}";

    private static string ToIbanDigits(string text) =>
        string.Concat(text.Select(c => char.IsLetter(c) ? (c - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString()));

    private static string Slug(string name) =>
        new string(name.ToLowerInvariant().Normalize(NormalizationForm.FormD).Where(char.IsAsciiLetterLower).ToArray());

    /// <summary>Tre lettere del cognome o del nome secondo la regola del codice fiscale (consonanti, poi vocali, poi X).</summary>
    private static string Consonants(string name, bool isFirstName)
    {
        string letters = Slug(name).ToUpperInvariant();
        string consonants = new(letters.Where(c => !"AEIOU".Contains(c)).ToArray());
        string vowels = new(letters.Where(c => "AEIOU".Contains(c)).ToArray());

        if (isFirstName && consonants.Length >= 4)
        {
            return $"{consonants[0]}{consonants[2]}{consonants[3]}";
        }

        return (consonants + vowels + "XXX")[..3];
    }

    private static char TaxCodeCheck(string body)
    {
        int[] odd = [1, 0, 5, 7, 9, 13, 15, 17, 19, 21, 2, 4, 18, 20, 11, 3, 6, 8, 12, 14, 16, 10, 22, 25, 24, 23];
        int sum = 0;

        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            int value = char.IsDigit(c) ? c - '0' : c - 'A';

            // Posizioni dispari (1-based) con la tabella dedicata, pari con il valore semplice.
            sum += i % 2 == 0 ? odd[value] : value;
        }

        return (char)('A' + sum % 26);
    }
}
