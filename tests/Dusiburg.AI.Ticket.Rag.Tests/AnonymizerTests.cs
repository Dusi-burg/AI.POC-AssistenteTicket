using Dusiburg.AI.Ticket.Rag.Cleaning;

namespace Dusiburg.AI.Ticket.Rag.Tests;

/// <summary>Anonimizzazione (fase-5.md §2): un test per tipo di dato personale, più ciò che non va toccato.</summary>
public class AnonymizerTests
{
    [TestCase("scrivete a anna.colombo@gruppoalimentaresole.example.it", "scrivete a [EMAIL]")]
    [TestCase("il sito è www.farmaciacentrale-lodi.example.it", "il sito è [SITO]")]
    [TestCase("IBAN è IT82T7476522708428791211999", "IBAN è [IBAN]")]
    [TestCase("IBAN IT82 T747 6522 7084 2879 1211 999", "IBAN [IBAN]")]
    [TestCase("la chiave è da_live_l1bvwku71mxq7u6d0o3n8hmxzbvhsu2o", "la chiave è [TOKEN]")]
    [TestCase("hash 3f9a1c7e5b2d4f6a8c0e1b3d5f7a9c2e4b6d", "hash [TOKEN]")]
    [TestCase("codice fiscale BNDPLA91C07Z923I, se serve", "codice fiscale [CF], se serve")]
    [TestCase("C.F. e P.IVA 98650160009", "C.F. e P.IVA [PIVA]")]
    [TestCase("P.IVA IT07794760004", "P.IVA [PIVA]")]
    [TestCase("Tel. 02 25279401 - Cell. 351 8898614", "Tel. [TELEFONO] - Cell. [TELEFONO]")]
    [TestCase("chiamate il +39 0471 513899", "chiamate il [TELEFONO]")]
    public void Anonymize_PersonalData_BecomesAPlaceholder(string text, string expected)
    {
        //SUT
        string anonymized = Anonymizer.Anonymize(text, KnownNames.None);

        Assert.That(anonymized, Is.EqualTo(expected));
    }

    [TestCase("errore FF-E2041 dalla 5.1.2, build 20261016")]
    [TestCase("cancellati 20.000 documenti, disco da 1 TB, coda di 100 elementi")]
    [TestCase("ticket HT-2025-0041 e articolo KB-FF-0004, nota RN-FF-4.8.5-04")]
    [TestCase("scadenza il 15/01/2026 alle 10:30")]
    public void Anonymize_CodesVersionsAndNumbers_AreUntouched(string text)
    {
        //SUT
        string anonymized = Anonymizer.Anonymize(text, KnownNames.None);

        Assert.That(anonymized, Is.EqualTo(text));
    }

    [Test]
    public void Anonymize_KnownNames_ReplacedAlsoInPartsButOnlyCapitalized()
    {
        //SETUP
        KnownNames names = KnownNames.Of("Elena Sala", "Farmacia Centrale Lodi S.r.l.");

        //SUT
        string anonymized = Anonymizer.Anonymize("Elena Sala di Farmacia Centrale Lodi S.r.l.: Gentile Elena, la sala server è ok. Sig.ra Sala", names);

        Assert.That(anonymized, Is.EqualTo("[NOME] di [CLIENTE]: Gentile [NOME], la sala server è ok. Sig.ra [NOME]"));
    }
}
