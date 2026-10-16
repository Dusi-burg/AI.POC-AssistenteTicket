using System.Text.RegularExpressions;
using Dusiburg.AI.Ticket.Data.Seed;
using Dusiburg.AI.Ticket.DataGen;
using Dusiburg.AI.Ticket.DbInit;
using Dusiburg.AI.Ticket.Shared.Demo;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Data.Tests;

/// <summary>
/// Coerenza dei dati simulati di <c>data/</c> (fase-3.md §4): versioni, codici errore, fonti citate, bug noti, test set disgiunto
/// dall'indice, nessun anacronismo, e <c>tickets.json</c> uguale a una rigenerazione. Nessun database.
/// </summary>
public partial class SeedDataCoherenceTests
{
    private static readonly Lazy<SeedData> Loaded = new(TicketDatabaseInitializer.LoadSeedData);

    private static SeedData Data => Loaded.Value;

    private static IReadOnlyList<GeneratedTicket> Tickets => Data.Tickets!.Tickets;

    [Test]
    public void Load_ExpandsTheTemplatesOfBothProducts()
    {
        //SUT
        SeedData data = Data;

        Assert.That(data.Customers, Has.Count.EqualTo(40), "PLAN §6.1: ~40 clienti");
        Assert.That(data.KbArticles, Has.Count.InRange(40, 60), "PLAN §6.1: ~40–60 articoli");
        Assert.That(data.ReleaseNotes, Has.Count.InRange(55, 65), "PLAN §6.1: ~60 note");
        Assert.That(data.KbArticles.Select(a => a.Code), Is.Unique);
        Assert.That(data.ReleaseNotes.Select(n => n.Code), Is.Unique);
        Assert.That(data.Customers.Select(c => c.EmailDomain), Is.Unique);
        Assert.That(data.Cases.Select(c => (c.Code, c.Product)), Is.Unique);
        Assert.That(AllKbText().Concat(data.Cases.SelectMany(c => c.Kb)), Has.None.Contains("{P}").And.None.Contains("{prodotto}"));
    }

    [Test]
    public void Versions_CitedEverywhereExistAndRangesAreValid()
    {
        //SETUP
        IEnumerable<(string Product, string? From, string? To)> ranges =
        [
            .. Data.Cases.Select(c => (c.Product, c.VersionFrom, c.VersionTo)),
            .. Data.KbArticles.Select(a => (a.Products.Single(), a.VersionFrom, a.VersionTo)),
        ];

        //SUT
        Assert.That(Data.Customers.Where(c => c.Version is not null), Has.All.Matches<CustomerDefinition>(c => Exists(c.Product, c.Version!)));
        Assert.That(Data.ReleaseNotes, Has.All.Matches<ReleaseNoteDefinition>(n => Exists(n.Product, n.Version)));
        Assert.That(ranges, Has.All.Matches<(string Product, string? From, string? To)>(r =>
            (r.From is null || Exists(r.Product, r.From)) && (r.To is null || Exists(r.Product, r.To))
            && (r.From is null || r.To is null || Sequence(r.Product, r.From) <= Sequence(r.Product, r.To))));
        Assert.That(Data.Customers.Where(c => c.Mode == DeploymentMode.Saas),
            Has.All.Matches<CustomerDefinition>(c => c.Version == DemoCatalog.Product(c.Product).Versions[^1].Version), "i clienti SaaS sono sull'ultima versione");
    }

    [Test]
    public void Tickets_NoPlaceholderLeftUnexpanded()
    {
        //SUT
        List<string> unexpanded = [.. Tickets
            .Where(t => new[] { t.Subject, t.Description, t.Resolution?.Cause ?? "", t.Resolution?.Solution ?? "" }
                .Concat(t.Messages.Select(m => m.Body))
                .Any(text => PlaceholderPattern().IsMatch(text)))
            .Select(t => $"{t.Number}: {t.Subject}")];

        // Fase 5: 260 oggetti avevano ancora "{codice}" perché il generatore non espandeva l'oggetto.
        Assert.That(unexpanded, Is.Empty);
    }

    [Test]
    public void ErrorCodes_EveryCodeCitedIsDocumentedInKbOrReleaseNotes()
    {
        //SETUP
        HashSet<string> documented = [.. AllKbText().Concat(Data.ReleaseNotes.Select(n => n.Body)).SelectMany(ErrorCodesIn)];

        //SUT
        IEnumerable<string> cited =
        [
            .. Data.Cases.SelectMany(c => CaseTexts(c).Append(c.ErrorCode ?? string.Empty)).SelectMany(ErrorCodesIn),
            .. Tickets.SelectMany(t => t.Messages.Select(m => m.Body).Append(t.Resolution?.Solution ?? string.Empty)).SelectMany(ErrorCodesIn),
            .. DemoCatalog.ErrorCodes.Select(e => e.Code),
        ];

        Assert.That(cited.Distinct(), Is.SubsetOf(documented));
    }

    [Test]
    public void Sources_CitedByCasesExistForTheSameProduct()
    {
        //SETUP
        HashSet<string> articles = [.. Data.KbArticles.Select(a => a.Code)];
        HashSet<string> notes = [.. Data.ReleaseNotes.Select(n => n.Code)];

        //SUT
        Assert.That(Data.Cases.SelectMany(c => c.Kb).Distinct(), Is.SubsetOf(articles));
        Assert.That(Data.Cases.SelectMany(c => c.ReleaseNotes).Distinct(), Is.SubsetOf(notes));
        Assert.That(Data.Cases, Has.All.Matches<CaseInstance>(c =>
            c.Kb.All(code => code.StartsWith($"KB-{c.Product}-", StringComparison.Ordinal))
            && c.ReleaseNotes.All(code => code.StartsWith($"RN-{c.Product}-", StringComparison.Ordinal))));
        Assert.That(Data.Cases, Has.All.Matches<CaseInstance>(c => c.Kb.Count + c.ReleaseNotes.Count > 0 || c.Definition.Category is "indicizzazione" or "prestazioni" or "backup"),
            "ogni caso ha almeno una fonte documentale, salvo quelli che vivono solo nello storico");
    }

    [Test]
    public void KnownIssues_FixedInALaterVersionHaveTheFixNote()
    {
        //SETUP
        IEnumerable<(ReleaseNoteDefinition Note, string FixedIn)> announced = Data.ReleaseNotes
            .Where(n => n.Type == ReleaseNoteType.KnownIssue)
            .Select(n => (n, FixedIn: FixedInPattern().Match(n.Body).Groups[1].Value))
            .Where(x => x.FixedIn.Length > 0);

        //SUT
        Assert.That(announced, Is.Not.Empty);
        Assert.That(announced, Has.All.Matches<(ReleaseNoteDefinition Note, string FixedIn)>(x =>
            Sequence(x.Note.Product, x.FixedIn) > Sequence(x.Note.Product, x.Note.Version)
            && Data.ReleaseNotes.Any(fix => fix.Product == x.Note.Product && fix.Version == x.FixedIn && fix.Type == ReleaseNoteType.Fix)));
    }

    [Test]
    public void Cases_HaveUsableVariants()
    {
        //SUT
        IEnumerable<CaseDefinition> cases = Data.Cases.Select(c => c.Definition).Distinct();

        Assert.That(cases.Count(), Is.EqualTo(54));
        Assert.That(cases, Has.All.Matches<CaseDefinition>(c => c.Symptoms.Count >= 3 && c.Subjects.Count > 0 && c.Solutions.Count > 0 && c.Replies.Count > 0 && c.Weight > 0));
        Assert.That(cases.Where(c => c.ErrorCode is null),
            Has.None.Matches<CaseDefinition>(c => CaseDefinitionTexts(c).Any(t => t.Contains("{codice}", StringComparison.Ordinal))),
            "{codice} solo nei casi con un codice errore");
    }

    [Test]
    public void TestSet_IsDisjointFromTheIndex()
    {
        //SETUP
        List<GeneratedTicket> indexed = [.. Tickets.Where(t => !t.IsTestSet)];
        List<GeneratedTicket> testSet = [.. Tickets.Where(t => t.IsTestSet)];

        //SUT
        Assert.That(indexed, Has.Count.EqualTo(TicketGenerator.IndexedTickets));
        Assert.That(testSet, Has.Count.EqualTo(150));
        Assert.That(indexed, Has.None.Matches<GeneratedTicket>(t => t.TrapKind is not null));
        Assert.That(testSet.Count(t => t.TrapKind is not null), Is.InRange(10, 15));
        Assert.That(Data.Cases, Has.None.Matches<CaseInstance>(c =>
            indexed.Any(t => t.Description.Contains(c.Expand(c.Definition.ReservedSymptom, null)[1..], StringComparison.Ordinal))),
            "la variante riservata al test set non compare nei ticket indicizzati");
        Assert.That(indexed.Select(t => t.CaseCode).Distinct().Count(), Is.EqualTo(54), "ogni caso è nell'indice");
        Assert.That(testSet.Where(t => t.TrapKind is null).Select(t => t.CaseCode).Distinct().Count(), Is.EqualTo(54), "ogni caso è nel test set");
    }

    [Test]
    public void Tickets_StatesAndDuplicatesAreRealistic()
    {
        //SETUP
        List<GeneratedTicket> indexed = [.. Tickets.Where(t => !t.IsTestSet)];
        Dictionary<string, GeneratedTicket> byNumber = Tickets.ToDictionary(t => t.Number);

        //SUT
        double withSolution = (double)indexed.Count(t => t.Resolution is not null) / indexed.Count;
        List<GeneratedTicket> duplicates = [.. indexed.Where(t => t.DuplicateOf is not null)];

        Assert.That(withSolution, Is.InRange(0.75, 0.9), "PLAN §6.1: ~80% risolti con soluzione");
        Assert.That(indexed.Count(t => t.State == TicketState.Unresolved), Is.GreaterThan(40));
        Assert.That(duplicates, Has.Count.EqualTo(TicketGenerator.Duplicates));
        Assert.That(duplicates, Has.All.Matches<GeneratedTicket>(d =>
            byNumber[d.DuplicateOf!].OpenedAt < d.OpenedAt && byNumber[d.DuplicateOf!].Customer == d.Customer && byNumber[d.DuplicateOf!].CaseCode == d.CaseCode
            && d.Messages.Any(m => m.Body.Contains(d.DuplicateOf!, StringComparison.Ordinal))));
        Assert.That(Tickets.Where(t => t.State == TicketState.Resolved && t.DuplicateOf is null), Has.All.Matches<GeneratedTicket>(t => t.Resolution is not null));
    }

    [Test]
    public void Tickets_AreConsistentWithCustomersVersionsAndDates()
    {
        //SETUP
        Dictionary<string, CustomerDefinition> customers = Data.Customers.ToDictionary(c => c.EmailDomain);

        //SUT
        Assert.That(Tickets, Has.All.Matches<GeneratedTicket>(t =>
            customers[t.Customer].Product == t.Product && customers[t.Customer].Mode == t.Mode));
        Assert.That(Tickets.Where(t => t.Version is not null), Has.All.Matches<GeneratedTicket>(t =>
            DemoCatalog.Product(t.Product).Versions.Single(v => v.Version == t.Version).ReleasedOn <= DateOnly.FromDateTime(t.OpenedAt.Date)));
        Assert.That(Tickets.Where(t => t.TrapKind is null && t.Version is not null), Has.All.Matches<GeneratedTicket>(InCaseRange));
        Assert.That(Tickets, Has.None.Matches<GeneratedTicket>(IsAnachronistic), "nessun messaggio cita una versione non ancora rilasciata");
        Assert.That(Tickets.SelectMany(t => t.Messages).Where(m => m.Author == MessageAuthor.Agent), Has.All.Matches<GeneratedMessage>(InOfficeHours));
        Assert.That(Tickets.Select(t => t.Number), Is.Unique.And.All.Match(@"^HT-20\d\d-\d{4}$"));
    }

    [Test]
    public void Tickets_ContainFictitiousPersonalDataForTheAnonymization()
    {
        //SETUP
        List<string> texts = [.. Tickets.SelectMany(t => t.Messages).Select(m => m.Body)];

        //SUT
        Assert.That(texts.Count(t => PhonePattern().IsMatch(t)), Is.GreaterThan(200), "telefoni nelle firme");
        Assert.That(texts.Count(t => VatPattern().IsMatch(t)), Is.GreaterThan(20), "partite IVA (ufficio 000)");
        Assert.That(texts.Count(t => IbanPattern().IsMatch(t) || TokenPattern().IsMatch(t) || TaxCodePattern().IsMatch(t)), Is.GreaterThan(5),
            "IBAN, token e codici fiscali incollati per errore");
    }

    [Test]
    public void TicketsFile_IsUpToDateWithTheTexts()
    {
        //SETUP
        string versioned = File.ReadAllText(Path.Combine(SeedData.FindDataDirectory(AppContext.BaseDirectory)!, SeedData.TicketsFileName));

        //SUT
        string regenerated = TicketsJson.Serialize(new TicketGenerator(Data).Generate());

        Assert.That(TicketsJson.Normalize(versioned) == TicketsJson.Normalize(regenerated), Is.True,
            "data/tickets.json non corrisponde ai testi: rigenerarlo con tools/Dusiburg.AI.Ticket.DataGen");
    }

    private static bool InCaseRange(GeneratedTicket ticket)
    {
        CaseInstance instance = Data.Cases.Single(c => c.Code == ticket.CaseCode && c.Product == ticket.Product);
        short sequence = Sequence(ticket.Product, ticket.Version!);

        return instance.Definition.Modes.Contains(ticket.Mode)
            && (instance.VersionFrom is null || Sequence(ticket.Product, instance.VersionFrom) <= sequence)
            && (instance.VersionTo is null || sequence <= Sequence(ticket.Product, instance.VersionTo));
    }

    private static bool IsAnachronistic(GeneratedTicket ticket) =>
        ticket.Messages.Any(m => VersionMentions.IsAnachronistic(m.Body, ticket.Product, DateOnly.FromDateTime(m.SentAt.Date)))
        || (ticket.Resolution is { } resolution && ticket.ClosedAt is { } closedAt
            && VersionMentions.IsAnachronistic($"{resolution.Cause} {resolution.Solution}", ticket.Product, DateOnly.FromDateTime(closedAt.Date)));

    private static bool InOfficeHours(GeneratedMessage message)
    {
        TimeSpan time = message.SentAt.TimeOfDay;

        return message.SentAt.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && time >= new TimeSpan(8, 30, 0) && time <= new TimeSpan(18, 30, 0);
    }

    private static IEnumerable<string> AllKbText() =>
        Data.KbArticles.SelectMany(a => a.Sections.SelectMany(s => new[] { s.Title, s.Body }).Append(a.Title).Append(a.Code));

    private static IEnumerable<string> CaseTexts(CaseInstance instance) =>
        CaseDefinitionTexts(instance.Definition).Select(text => instance.Expand(text, null));

    private static IEnumerable<string> CaseDefinitionTexts(CaseDefinition definition) =>
    [
        .. definition.Subjects, .. definition.Symptoms, .. definition.Details, .. definition.Questions, .. definition.FollowUps,
        definition.Cause, .. definition.Solutions, .. definition.Replies,
    ];

    private static IEnumerable<string> ErrorCodesIn(string text) => ErrorCodePattern().Matches(text).Select(m => m.Value);

    private static bool Exists(string product, string version) => DemoCatalog.Product(product).Versions.Any(v => v.Version == version);

    private static short Sequence(string product, string version) => DemoCatalog.Product(product).SequenceOf(version);

    [GeneratedRegex(@"\b(?:FF|DA)-E\d{4}\b")]
    private static partial Regex ErrorCodePattern();

    /// <summary>Segnaposto dei testi di data/ (<c>{codice}</c>, <c>{versione}</c>, <c>{P}</c>, <c>{nome}</c>…).</summary>
    [GeneratedRegex(@"\{[A-Za-z]+\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"Corretto nella (\d+\.\d+\.\d+)")]
    private static partial Regex FixedInPattern();

    [GeneratedRegex(@"\b0\d{1,3} \d{6,8}\b|\b3\d{2} \d{7}\b")]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"\b\d{7}000\d\b")]
    private static partial Regex VatPattern();

    [GeneratedRegex(@"\bIT\d{2}[A-Z]\d{22}\b")]
    private static partial Regex IbanPattern();

    [GeneratedRegex(@"\b(?:ff|da)_live_[a-z0-9]{32}\b")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\b[A-Z]{6}\d{2}[A-Z]\d{2}Z9\d{2}[A-Z]\b")]
    private static partial Regex TaxCodePattern();
}
