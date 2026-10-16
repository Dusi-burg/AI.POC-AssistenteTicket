using System.Text;
using System.Text.RegularExpressions;
using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Rag.Cleaning;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Rag.Indexing;

/// <summary>
/// Dalle fonti ai frammenti indicizzati (fase-5.md §3): un chunk per ticket risolto, uno per sezione di articolo KB, uno per nota
/// di rilascio, con i metadati di applicabilità copiati dalla fonte. Il testo è già pulito e anonimizzato: è quello che vedrà il
/// modello nel prompt della Fase 6. Nessun accesso al database: lo usa <see cref="IndexingPipeline"/> e lo provano i test.
/// </summary>
public static partial class ChunkBuilder
{
    /// <summary>Solo ticket con una soluzione propria: niente test set, aperti, senza soluzione o duplicati di un altro ticket.</summary>
    public static bool IsIndexable(HistoricalTicket ticket) =>
        !ticket.IsTestSet && ticket.State == TicketState.Resolved && ticket.Resolution is not null && ticket.DuplicateOfId is null;

    /// <summary>
    /// Oggetto, primo messaggio del cliente, risposte del cliente alle domande dell'operatore, causa e soluzione. Nessun intervallo
    /// di versioni: la versione su cui si è visto il problema non dice fin dove vale la soluzione, e trattarla come intervallo
    /// penalizzava i ticket dello stesso caso su versioni vicine (precisione del prompt dal 96,7% all'87,4%, fase-5.md "Esito").
    /// </summary>
    public static Chunk ForTicket(HistoricalTicket ticket, string customerName)
    {
        if (!IsIndexable(ticket))
        {
            throw new ArgumentException($"Il ticket {ticket.Number} non è indicizzabile.", nameof(ticket));
        }

        KnownNames names = KnownNames.Of(ticket.ContactName, customerName);
        List<TicketMessage> thread = [.. ticket.Messages.OrderBy(m => m.Sequence)];
        var text = new StringBuilder();

        text.AppendLine(Anonymizer.Anonymize(ticket.Subject.Trim(), names));
        text.AppendLine(SourceText.CustomerMessage(thread.FirstOrDefault()?.Body ?? ticket.Description, names));

        // Le risposte del cliente seguite da un messaggio dell'operatore: dettagli chiesti (versione, log), non i ringraziamenti finali.
        foreach (TicketMessage detail in thread.Skip(1).Where((m, i) => m.Author == MessageAuthor.Customer && i + 2 < thread.Count && thread[i + 2].Author == MessageAuthor.Agent))
        {
            text.AppendLine($"Dettagli: {SourceText.CustomerMessage(detail.Body, names)}");
        }

        if (!string.IsNullOrWhiteSpace(ticket.Resolution!.Cause))
        {
            text.AppendLine().AppendLine($"Causa: {Anonymizer.Anonymize(ticket.Resolution.Cause.Trim(), names)}");
        }
        else
        {
            text.AppendLine();
        }

        text.Append($"Soluzione: {Anonymizer.Anonymize(ticket.Resolution.Solution.Trim(), names)}");

        return new Chunk
        {
            Source = ChunkSource.HistoricalTicket,
            HistoricalTicketId = ticket.Id,
            Section = 1,
            ProductId = ticket.ProductId,
            DeploymentMode = ticket.DeploymentMode,
            Text = Normalize(text.ToString())
        };
    }

    /// <summary>Un chunk per sezione (<c>## Titolo</c> del corpo), preceduto dal titolo dell'articolo.</summary>
    public static IEnumerable<Chunk> ForKbArticle(KbArticle article)
    {
        IReadOnlyList<(string Title, string Body)> sections = KbSections(article.Body);

        return sections.Select((section, index) => new Chunk
        {
            Source = ChunkSource.KbArticle,
            KbArticleId = article.Id,
            Section = (short)(index + 1),
            ProductId = article.ProductId,
            DeploymentMode = article.DeploymentMode,
            VersionFromId = article.VersionFromId,
            VersionToId = article.VersionToId,
            Text = Normalize($"{article.Title} — {section.Title}\n{section.Body}")
        });
    }

    /// <summary>
    /// Applicabilità di una nota: un problema noto o una novità valgono dalla loro versione in poi; una correzione interessa chi è
    /// ancora sulle versioni precedenti (deve aggiornare), quindi fino alla versione prima di quella della nota.
    /// </summary>
    public static Chunk ForReleaseNote(ReleaseNote note, ProductVersion version, ProductVersion? previous, string productName) => new()
    {
        Source = ChunkSource.ReleaseNote,
        ReleaseNoteId = note.Id,
        Section = 1,
        ProductId = version.ProductId,
        DeploymentMode = null,
        VersionFromId = note.Type == ReleaseNoteType.Fix ? null : version.Id,
        VersionToId = note.Type == ReleaseNoteType.Fix ? previous?.Id ?? version.Id : null,
        Text = Normalize($"{productName} {version.Version} — {TypeLabel(note.Type)}: {note.Title}\n{note.Body}")
    };

    /// <summary>Le sezioni scritte dall'importazione come <c>## Titolo\n\nTesto</c>; un corpo senza titoli è una sezione sola.</summary>
    internal static IReadOnlyList<(string Title, string Body)> KbSections(string body)
    {
        MatchCollection headings = SectionHeading().Matches(body);

        if (headings.Count == 0)
        {
            return [(string.Empty, body.Trim())];
        }

        return [.. headings.Select((heading, index) =>
        {
            int start = heading.Index + heading.Length;
            int end = index + 1 < headings.Count ? headings[index + 1].Index : body.Length;

            return (heading.Groups[1].Value.Trim(), body[start..end].Trim());
        })];
    }

    private static string TypeLabel(ReleaseNoteType type) => type switch
    {
        ReleaseNoteType.KnownIssue => "Problema noto",
        ReleaseNoteType.Fix => "Correzione",
        ReleaseNoteType.Feature => "Novità",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private static string Normalize(string text) => MultipleBlankLines().Replace(text.Replace("\r\n", "\n", StringComparison.Ordinal), "\n\n").Trim();

    [GeneratedRegex(@"^## (.+)$", RegexOptions.Multiline)]
    private static partial Regex SectionHeading();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultipleBlankLines();
}
