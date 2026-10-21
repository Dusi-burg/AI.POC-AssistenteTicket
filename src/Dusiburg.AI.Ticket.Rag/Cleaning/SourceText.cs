namespace Dusiburg.AI.Ticket.Rag.Cleaning;

/// <summary>
/// La stessa trasformazione per il testo indicizzato e per la query (fase-5.md §2): un messaggio del cliente pulito e anonimizzato,
/// così documento e query si confrontano su testo omogeneo.
/// </summary>
public static class SourceText
{
    public static string CustomerMessage(string body, KnownNames names) =>
        Anonymizer.Anonymize(MessageCleaner.Clean(body, names.People), names);

    /// <summary>Testo della ricerca per un ticket in arrivo: oggetto e primo messaggio del cliente.</summary>
    public static string Query(string subject, string body, KnownNames names) =>
        $"{Anonymizer.Anonymize(subject.Trim(), names)}\n{CustomerMessage(body, names)}";
}
