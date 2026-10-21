using System.Globalization;
using Dusiburg.AI.Ticket.Data.Seed;

namespace Dusiburg.AI.Ticket.DataGen;

/// <summary>Contesto di un ticket per comporre i testi: chi scrive, per quale cliente, quale caso e versione.</summary>
internal sealed record TicketVoice(CustomerDefinition Customer, string VatNumber, Contact Contact, string Operator, CaseInstance Case, string? Version);

/// <summary>
/// Compone i messaggi a partire dalle varianti dei casi e dalle frasi di <c>rumore.json</c>: il primo messaggio del cliente è
/// fatto di parti indipendenti (apertura, sintomo, 0–2 dettagli, allegati, firma, disclaimer), così due ticket dello stesso caso
/// non coincidono quasi mai. Le varianti si scelgono fra quelle che citano solo versioni già rilasciate alla data del messaggio.
/// </summary>
internal sealed class MessageComposer(NoiseFile noise, Random random)
{
    public string Subject(TicketVoice voice, DateOnly date) =>
        Decorate(voice.Case.Expand(PickValid(voice.Case.Definition.Subjects, voice, date), voice.Version));

    public string FirstCustomerMessage(TicketVoice voice, string symptom, DateOnly date)
    {
        List<string> body = [Capitalize(voice.Case.Expand(symptom, voice.Version))];

        IReadOnlyList<string> usableDetails = [.. Valid(voice.Case.Definition.Details, voice, date)
            .Where(d => voice.Version is not null || !d.Contains("{versione}", StringComparison.Ordinal))];
        body.AddRange(random.PickDistinct(usableDetails, random.Next(0, 3)).Select(d => voice.Case.Expand(d, voice.Version)));

        if (random.Chance(0.12))
        {
            body.Add(random.Pick(noise.AttachmentMentions));
        }

        List<string> parts = [random.Pick(noise.CustomerOpeners), string.Join(" ", body)];

        if (random.Chance(0.04))
        {
            parts.Add(Fill(random.Pick(noise.PastedSecrets), voice));
        }

        parts.Add(random.Pick(noise.CustomerClosers));
        parts.Add(string.Empty);
        parts.Add(Fill(random.Pick(noise.Signatures), voice));

        if (random.Chance(0.15))
        {
            parts.Add(string.Empty);
            parts.Add(random.Pick(noise.Disclaimers));
        }

        return Join(parts);
    }

    public string AgentQuestions(TicketVoice voice, DateOnly date)
    {
        List<string> questions = [.. random.PickDistinct(Valid(voice.Case.Definition.Questions, voice, date), 2).Select(q => voice.Case.Expand(q, voice.Version))];

        if (questions.Count == 0 || random.Chance(0.4))
        {
            questions.Add(Fill(random.Pick(noise.AgentGenericQuestions), voice));
        }

        string list = string.Join("\n", questions.Distinct().Select(q => $"- {q}"));

        return AgentMessage(voice, $"{Capitalize(random.Pick(noise.AgentInfoRequests))}\n{list}", noise.AgentQuestionClosings);
    }

    public string CustomerFollowUp(TicketVoice voice, string previousAgentMessage, DateTimeOffset previousSentAt)
    {
        DateOnly date = DateOnly.FromDateTime(previousSentAt.Date);
        IReadOnlyList<string> answers = voice.Case.Definition.FollowUps.Count > 0 ? voice.Case.Definition.FollowUps : voice.Case.Definition.Details;
        string answer = voice.Case.Expand(PickValid(answers, voice, date), voice.Version ?? "l'ultima");

        List<string> parts = [Join([random.Pick(noise.CustomerFollowUpPrefixes), answer]), voice.Contact.FirstName];

        if (random.Chance(0.25))
        {
            parts.Add(string.Empty);
            parts.Add(Fill(random.Pick(noise.QuoteHeaders), voice, previousSentAt));
            parts.AddRange(previousAgentMessage.Split('\n').Take(4).Select(line => $"> {line}"));
        }

        return Join(parts);
    }

    public string AgentSolution(TicketVoice voice, DateOnly date) =>
        AgentMessage(voice, Capitalize(voice.Case.Expand(PickValid(voice.Case.Definition.Replies, voice, date), voice.Version)));

    public string AgentUnresolved(TicketVoice voice) => AgentMessage(voice, random.Pick(noise.AgentUnresolvedReplies));

    public string AgentDuplicate(string originalNumber) =>
        random.Pick(noise.AgentDuplicateReplies).Replace("{numero}", originalNumber, StringComparison.Ordinal);

    public string CustomerThanks(TicketVoice voice) => $"{random.Pick(noise.CustomerThanks)}\n{voice.Contact.FirstName}";

    public GeneratedResolution Resolution(TicketVoice voice, DateOnly date)
    {
        string cause = voice.Case.Expand(voice.Case.Definition.Cause, voice.Version);
        bool withoutCause = random.Chance(0.1) || VersionMentions.IsAnachronistic(cause, voice.Case.Product, date);

        return new GeneratedResolution(
            withoutCause ? null : cause,
            voice.Case.Expand(PickValid(voice.Case.Definition.Solutions, voice, date), voice.Version),
            IsVerified: !random.Chance(0.1));
    }

    /// <summary>Le varianti che alla data citano solo versioni già rilasciate.</summary>
    private static List<string> Valid(IReadOnlyList<string> variants, TicketVoice voice, DateOnly date) =>
        [.. variants.Where(v => !VersionMentions.IsAnachronistic(voice.Case.Expand(v, voice.Version), voice.Case.Product, date))];

    private string PickValid(IReadOnlyList<string> variants, TicketVoice voice, DateOnly date)
    {
        List<string> valid = Valid(variants, voice, date);

        return valid.Count > 0
            ? random.Pick(valid)
            : throw new InvalidOperationException($"Caso {voice.Case.Code}: nessuna variante valida al {date:yyyy-MM-dd} (tutte citano versioni non ancora rilasciate).");
    }

    private string Decorate(string subject) =>
        random.Chance(0.08) ? $"URGENTE - {subject}" : random.Chance(0.05) ? $"R: {subject}" : subject;

    /// <summary>Saluto, corpo e chiusura; le domande al cliente hanno chiusure proprie (non "se il problema si ripresenta").</summary>
    private string AgentMessage(TicketVoice voice, string body, IReadOnlyList<string>? closings = null) =>
        $"{Fill(random.Pick(noise.AgentGreetings), voice)}\n\n{body}\n\n{Fill(random.Pick(closings ?? noise.AgentClosings), voice)}";

    private string Fill(string template, TicketVoice voice, DateTimeOffset? date = null) => FillSecrets(template, voice)
        .Replace("{nome}", voice.Contact.FirstName, StringComparison.Ordinal)
        .Replace("{cognome}", voice.Contact.LastName, StringComparison.Ordinal)
        .Replace("{ruolo}", voice.Contact.Role, StringComparison.Ordinal)
        .Replace("{azienda}", voice.Customer.Name, StringComparison.Ordinal)
        .Replace("{telefono}", voice.Contact.Phone, StringComparison.Ordinal)
        .Replace("{cellulare}", voice.Contact.Mobile, StringComparison.Ordinal)
        .Replace("{email}", voice.Contact.Email, StringComparison.Ordinal)
        .Replace("{dominio}", voice.Customer.EmailDomain, StringComparison.Ordinal)
        .Replace("{piva}", voice.VatNumber, StringComparison.Ordinal)
        .Replace("{cf}", voice.Contact.TaxCode, StringComparison.Ordinal)
        .Replace("{operatore}", voice.Operator, StringComparison.Ordinal)
        .Replace("{prodotto}", SeedData.ProductName(voice.Customer.Product), StringComparison.Ordinal)
        .Replace("{data}", (date ?? DateTimeOffset.MinValue).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>IBAN e token si generano solo se il modello li usa: ogni estrazione consuma il generatore casuale.</summary>
    private string FillSecrets(string template, TicketVoice voice)
    {
        if (template.Contains("{iban}", StringComparison.Ordinal))
        {
            template = template.Replace("{iban}", FictitiousIdentity.Iban(random), StringComparison.Ordinal);
        }

        return template.Contains("{token}", StringComparison.Ordinal)
            ? template.Replace("{token}", FictitiousIdentity.ApiToken(random, voice.Customer.Product), StringComparison.Ordinal)
            : template;
    }

    /// <summary>Unisce le righe saltando le parti vuote iniziali e i doppi a capo.</summary>
    private static string Join(IEnumerable<string> parts)
    {
        List<string> lines = [];

        foreach (string part in parts)
        {
            if (part.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0))
            {
                continue;
            }

            lines.Add(part);
        }

        return string.Join("\n", lines).Trim();
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.GetCultureInfo("it-IT")) + text[1..];
}
