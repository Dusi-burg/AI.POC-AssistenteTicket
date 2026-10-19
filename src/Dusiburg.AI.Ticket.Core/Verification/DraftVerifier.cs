using System.Text.RegularExpressions;
using Dusiburg.AI.Ticket.Core.Drafting;
using Dusiburg.AI.Ticket.Rag.Retrieval;

namespace Dusiburg.AI.Ticket.Core.Verification;

/// <summary>Esito di una citazione del modello.</summary>
public sealed record VerifiedCitation(string Id, string Reason, bool IsVerified);

/// <param name="Answer">La risposta del modello senza la firma.</param>
/// <param name="UnsupportedTerms">Termini tecnici della risposta che non compaiono né nelle fonti né nel ticket.</param>
/// <param name="TechnicalTerms">Quanti termini tecnici ha la risposta (denominatore di <see cref="SupportRatio"/>).</param>
public sealed record VerificationResult(
    string Answer, IReadOnlyList<VerifiedCitation> Citations, IReadOnlyList<string> UnsupportedTerms, int TechnicalTerms, bool SignatureRemoved)
{
    /// <summary>Quota delle citazioni che corrispondono a fonti del prompt; 0 se il modello non ne cita.</summary>
    public double CitationRatio => Citations.Count == 0 ? 0 : (double)Citations.Count(c => c.IsVerified) / Citations.Count;

    /// <summary>Quota dei termini tecnici presenti nelle fonti o nel ticket; 1 se la risposta non ne ha.</summary>
    public double SupportRatio => TechnicalTerms == 0 ? 1 : 1 - (double)UnsupportedTerms.Count / TechnicalTerms;
}

/// <summary>
/// Verifica a valle (PLAN §5 Fase 6), deterministica: ogni fonte citata deve essere fra quelle del prompt e ogni termine tecnico
/// della risposta (codici errore, versioni, percorsi di menu, comandi) deve comparire nelle fonti o nel ticket. Toglie la firma,
/// che il modello aggiunge nonostante la regola (fase-0.md §5): resta il "Cordiali saluti," finale.
/// </summary>
public static partial class DraftVerifier
{
    public static VerificationResult Verify(ModelDraft draft, IReadOnlyList<RetrievedSource> promptSources, string ticketText)
    {
        HashSet<string> keys = [.. promptSources.Select(s => s.Key)];
        List<VerifiedCitation> citations = [.. draft.Sources
            .DistinctBy(s => s.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(s => new VerifiedCitation(s.Id.Trim(), s.Reason.Trim(), keys.Contains(s.Id.Trim())))];

        (string answer, bool signatureRemoved) = RemoveSignature(draft.Answer);
        string evidence = Normalize(string.Join("\n", promptSources.SelectMany(s => s.Chunks).Select(c => c.Chunk.Text).Append(ticketText)));
        List<string> terms = [.. TechnicalTerms(answer)];
        List<string> unsupported = [.. terms.Where(term => !evidence.Contains(Normalize(term), StringComparison.Ordinal))];

        return new VerificationResult(answer, citations, unsupported, terms.Count, signatureRemoved);
    }

    /// <summary>Codici errore, versioni, percorsi di menu (<c>Impostazioni &gt; Firma</c>) e comandi (<c>ffadmin receive --now</c>), distinti.</summary>
    internal static IEnumerable<string> TechnicalTerms(string text) =>
        ErrorCode().Matches(text).Select(m => m.Value)
            .Concat(Version().Matches(text).Select(m => m.Value))
            .Concat(MenuPath().Matches(text).Select(m => m.Value.Trim()))
            .Concat(Command().Matches(text).Select(m => m.Value.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Dopo l'ultima formula di chiusura non resta nulla: nome, ruolo, "Supporto tecnico Dusiburg". Senza formula si tolgono le
    /// righe finali che sono solo una firma dell'azienda.
    /// </summary>
    internal static (string Text, bool Removed) RemoveSignature(string answer)
    {
        List<string> lines = [.. answer.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd().Split('\n')];
        int closing = lines.FindLastIndex(line => Closing().IsMatch(line.Trim()));
        bool removed = false;

        if (closing >= 0 && closing >= lines.Count - 5 && closing < lines.Count - 1)
        {
            lines.RemoveRange(closing + 1, lines.Count - closing - 1);
            removed = true;
        }

        while (lines.Count > 1 && CompanySignature().IsMatch(lines[^1].Trim()))
        {
            lines.RemoveAt(lines.Count - 1);
            removed = true;
        }

        return (string.Join('\n', lines).TrimEnd(), removed);
    }

    private static string Normalize(string text) => Spaces().Replace(text.ToLowerInvariant().Replace('’', '\''), " ");

    [GeneratedRegex(@"\b(?:FF|DA)-E\d{4}\b")]
    private static partial Regex ErrorCode();

    [GeneratedRegex(@"\b\d+\.\d+\.\d+\b")]
    private static partial Regex Version();

    /// <summary>
    /// Due o più voci separate da <c>&gt;</c>: ogni voce comincia con una maiuscola ed è seguita al più da due parole minuscole
    /// (<c>Impostazioni &gt; Firma remota</c>), così il verbo che precede ("Accedere a ...") resta fuori.
    /// </summary>
    [GeneratedRegex(@"\b[A-ZÀ-Ú][\wà-ù'’]*(?: [a-zà-ù][\wà-ù'’]*){0,2}(?: > [A-ZÀ-Ú][\wà-ù'’]*(?: [a-zà-ù][\wà-ù'’]*){0,2})+")]
    private static partial Regex MenuPath();

    /// <summary>Il comando e fino a quattro argomenti: opzioni (<c>--now</c>), parole di almeno due lettere, segnaposto (<c>&lt;file&gt;</c>).</summary>
    [GeneratedRegex(@"\b(?:ffadmin|daadmin)(?: (?:--?[\w-]+|[a-z][\w.-]*\w|<[^>]+>)){1,4}")]
    private static partial Regex Command();

    [GeneratedRegex(@"^(?:cordiali saluti|distinti saluti|un cordiale saluto|un saluto|saluti|a presto)[,.!]?$", RegexOptions.IgnoreCase)]
    private static partial Regex Closing();

    [GeneratedRegex(@"^(?:il )?(?:team|supporto|assistenza|servizio clienti)\b[^.!?]*$|^dusiburg[^.!?]*$", RegexOptions.IgnoreCase)]
    private static partial Regex CompanySignature();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
