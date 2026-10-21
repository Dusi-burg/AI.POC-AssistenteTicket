using Dusiburg.AI.Ticket.Rag.Cleaning;

namespace Dusiburg.AI.Ticket.Rag.Tests;

/// <summary>Pulizia dei messaggi del cliente (fase-5.md §2): resta la descrizione del problema, spariscono saluti, firma, disclaimer e citazioni.</summary>
public class MessageCleanerTests
{
    private static readonly string[] Contact = ["Anna Colombo"];

    [Test]
    public void Clean_SignatureAfterClosing_KeepsOnlyTheProblem()
    {
        //SETUP
        const string body =
            "Buongiorno a tutti,\nNessuna fattura dei fornitori dal primo del mese, compare l'avviso FF-E3310.\n" +
            "Restiamo in attesa di un vostro riscontro.\n\nCordiali saluti\nAnna Colombo\nSistemi informativi - Gruppo Alimentare Sole S.p.A.\n" +
            "C.F. e P.IVA 98650160009";

        //SUT
        string cleaned = MessageCleaner.Clean(body, Contact);

        Assert.That(cleaned, Is.EqualTo("Nessuna fattura dei fornitori dal primo del mese, compare l'avviso FF-E3310."));
    }

    [Test]
    public void Clean_DashSignatureAndSmartphone_AreRemoved()
    {
        //SETUP
        const string body = "Spett.le supporto,\nLa numerazione è ripartita da un numero sbagliato.\nAttendo vostre indicazioni.\n\n--\nElena Sala\nCell. 349 8008799\nInviato da smartphone";

        //SUT
        string cleaned = MessageCleaner.Clean(body, ["Elena Sala"]);

        Assert.That(cleaned, Is.EqualTo("La numerazione è ripartita da un numero sbagliato."));
    }

    [Test]
    public void Clean_SignatureWithoutName_RecognizedFromShortLinesAndPhone()
    {
        //SETUP
        const string body = "Le fatture restano in invio e poi vanno in errore FF-E2050, tempo scaduto.\n\nUfficio amministrazione\nTrasporti Marchetti S.r.l.\n030 3955666";

        //SUT
        string cleaned = MessageCleaner.Clean(body, KnownNamesFor());

        Assert.That(cleaned, Is.EqualTo("Le fatture restano in invio e poi vanno in errore FF-E2050, tempo scaduto."));
    }

    [Test]
    public void Clean_Disclaimer_IsRemovedEvenWithoutSignature()
    {
        //SETUP
        const string body = "La ricerca non trova i documenti nuovi.\n\nAVVISO DI RISERVATEZZA: il contenuto di questo messaggio è riservato.";

        //SUT
        string cleaned = MessageCleaner.Clean(body, KnownNamesFor());

        Assert.That(cleaned, Is.EqualTo("La ricerca non trova i documenti nuovi."));
    }

    [TestCase("Ok, provo subito.\n\nIl 12/03/2026 Supporto Dusiburg Software ha scritto:\nGentile cliente, verifichi il certificato.")]
    [TestCase("Ok, provo subito.\n-----Messaggio originale-----\nDa: Supporto Dusiburg Software\nVerifichi il certificato.")]
    [TestCase("Ok, provo subito.\n> Gentile cliente,\n> verifichi il certificato.")]
    public void Clean_QuotedMessage_IsCut(string body)
    {
        //SUT
        string cleaned = MessageCleaner.Clean(body, KnownNamesFor());

        Assert.That(cleaned, Is.EqualTo("Ok, provo subito."));
    }

    [Test]
    public void Clean_InlineSignatureWithoutBlankLine_IsCutAtTheName()
    {
        //SETUP
        const string body = "Da stamattina l'invio si blocca con FF-E2041.\nAnna Colombo\nTel. 02 25279401";

        //SUT
        string cleaned = MessageCleaner.Clean(body, Contact);

        Assert.That(cleaned, Is.EqualTo("Da stamattina l'invio si blocca con FF-E2041."));
    }

    [Test]
    public void Clean_FirstNameMentionedInText_IsNotTakenForASignature()
    {
        //SETUP
        const string body = "Anna del reparto contabilità non riesce più ad accedere da ieri.\nIl problema riguarda solo lei.";

        //SUT
        string cleaned = MessageCleaner.Clean(body, Contact);

        Assert.That(cleaned, Is.EqualTo(body));
    }

    [Test]
    public void Clean_OnlyCourtesy_KeepsTheOriginalText()
    {
        //SUT
        string cleaned = MessageCleaner.Clean("Grazie mille per l'aiuto.", Contact);

        Assert.That(cleaned, Is.EqualTo("Grazie mille per l'aiuto."));
    }

    private static string[] KnownNamesFor() => [];
}
