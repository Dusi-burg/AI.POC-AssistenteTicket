using System.Globalization;
using Dusiburg.AI.Ticket.Data.Seed;
using Dusiburg.AI.Ticket.Shared.Demo;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.DataGen;

/// <summary>
/// Genera i ticket storici combinando i casi di <c>data/scenari</c> con clienti, versioni, date e rumore (fase-3.md §2). A parità
/// di testi e di seed il risultato è identico: <c>data/tickets.json</c> è versionato e un test lo confronta con una rigenerazione.
/// </summary>
public sealed class TicketGenerator
{
    public const int DefaultRandomSeed = 20261016;

    /// <summary>Ticket indicizzabili, duplicati compresi (PLAN §6.1: ~800–1.200).</summary>
    public const int IndexedTickets = 800;

    public const int Duplicates = 32;

    /// <summary>Ticket del test set presi dai casi con la variante riservata; i trabocchetti si aggiungono (PLAN §6.1: ~150).</summary>
    public const int TestSetFromCases = 135;

    public static readonly DateOnly FirstDay = new(2024, 9, 16);
    public static readonly DateOnly LastDay = new(2026, 10, 10);

    private const string OriginalNumberPlaceholder = "{numero-originale}";
    private static readonly TimeZoneInfo Rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");

    private readonly SeedData _data;
    private readonly int _randomSeed;
    private readonly Random _random;
    private readonly MessageComposer _composer;
    private readonly List<CustomerProfile> _customers;

    public TicketGenerator(SeedData data, int randomSeed = DefaultRandomSeed)
    {
        _data = data;
        _randomSeed = randomSeed;
        _random = new Random(randomSeed);
        _composer = new MessageComposer(data.Noise, _random);
        _customers = [.. data.Customers.Select(CreateProfile)];
    }

    public TicketsFile Generate()
    {
        IReadOnlyList<CaseSlot> slots = Slots();
        List<TicketDraft> drafts = [];

        foreach ((CaseSlot slot, int count) in Allocate(slots, IndexedTickets - Duplicates))
        {
            drafts.AddRange(Enumerable.Range(0, count).Select(_ => CaseTicket(slot, isTestSet: false)));
        }

        drafts.AddRange(DuplicatesOf(drafts));

        foreach ((CaseSlot slot, int count) in Allocate(slots, TestSetFromCases))
        {
            drafts.AddRange(Enumerable.Range(0, count).Select(_ => CaseTicket(slot, isTestSet: true)));
        }

        drafts.AddRange(_data.Traps.Select(TrapTicket));

        return new TicketsFile(_randomSeed, Number(drafts));
    }

    private CustomerProfile CreateProfile(CustomerDefinition customer)
    {
        IReadOnlyList<VersionPeriod> timeline = CustomerTimeline.Build(customer, _random, FirstDay, LastDay);
        string vat = FictitiousIdentity.VatNumber(_random);
        List<Contact> contacts = [.. Enumerable.Range(0, 2).Select(_ => FictitiousIdentity.Contact(
            _random, _random.Pick(_data.Noise.FirstNames), _random.Pick(_data.Noise.LastNames), _random.Pick(_data.Noise.Roles), customer.EmailDomain))];

        return new CustomerProfile(customer, timeline, vat, contacts);
    }

    /// <summary>Per ogni caso, i periodi in cui un cliente compatibile (prodotto, modalità) usava una versione dell'intervallo.</summary>
    private IReadOnlyList<CaseSlot> Slots() => [.. _data.Cases.Select(instance =>
    {
        short from = instance.VersionFrom is null ? (short)1 : Sequence(instance.Product, instance.VersionFrom);
        short to = instance.VersionTo is null ? short.MaxValue : Sequence(instance.Product, instance.VersionTo);

        DateOnly notBefore = instance.Definition.NotBefore ?? FirstDay;

        List<CaseWindow> windows = [.. _customers
            .Where(c => c.Definition.Product == instance.Product && instance.Definition.Modes.Contains(c.Definition.Mode))
            .SelectMany(c => c.Timeline
                .Where(p => p.Sequence >= from && p.Sequence <= to && p.To >= notBefore)
                .Select(p => new CaseWindow(c, p.From < notBefore ? p with { From = notBefore } : p)))];

        return windows.Count > 0
            ? new CaseSlot(instance, windows)
            : throw new InvalidOperationException($"Nessun cliente compatibile con il caso {instance.Code} ({instance.Product}).");
    })];

    /// <summary>Almeno un ticket per caso, il resto in proporzione al peso (i casi trasversali pesano meno per prodotto).</summary>
    private static IEnumerable<(CaseSlot Slot, int Count)> Allocate(IReadOnlyList<CaseSlot> slots, int total)
    {
        double[] weights = [.. slots.Select(s => s.Instance.Definition.Products.Count > 1 ? s.Instance.Definition.Weight * 0.6 : s.Instance.Definition.Weight)];
        int remaining = total - slots.Count;
        double[] shares = [.. weights.Select(w => remaining * w / weights.Sum())];
        int[] counts = [.. shares.Select(s => 1 + (int)Math.Floor(s))];

        // Resti più grandi per arrivare esattamente al totale (a parità, l'ordine dei file).
        foreach (int index in shares.Select((s, i) => (Remainder: s - Math.Floor(s), Index: i)).OrderByDescending(x => x.Remainder).ThenBy(x => x.Index)
                     .Take(total - counts.Sum()).Select(x => x.Index).ToList())
        {
            counts[index]++;
        }

        return slots.Select((slot, i) => (slot, counts[i]));
    }

    private TicketDraft CaseTicket(CaseSlot slot, bool isTestSet)
    {
        CaseWindow window = _random.PickWeighted(slot.Windows, w => w.Period.Days);
        DateTimeOffset openedAt = WorkingMoment(window.Period.From.AddDays(_random.Next(window.Period.Days)));
        bool versionUnknown = window.Customer.Definition.Version is null || _random.Chance(0.05);
        TicketVoice voice = Voice(window.Customer, slot.Instance, versionUnknown ? null : window.Period.Version);
        string symptom = isTestSet ? slot.Instance.Definition.ReservedSymptom : _random.Pick(slot.Instance.Definition.IndexedSymptoms);

        DateOnly openedOn = DateOnly.FromDateTime(openedAt.Date);
        TicketState state = isTestSet ? TicketState.Resolved : ChooseState(openedAt);
        List<GeneratedMessage> messages = [new(MessageAuthor.Customer, _composer.FirstCustomerMessage(voice, symptom, openedOn), openedAt)];
        GeneratedResolution? resolution = null;

        switch (state)
        {
            case TicketState.Resolved:
                AddQuestionRound(voice, messages, chance: slot.Instance.Definition.Questions.Count > 0 ? 0.55 : 0.3);
                // La data dell'ultimo messaggio è un limite prudente: la risposta parte dopo.
                messages.Add(Next(messages, MessageAuthor.Agent, _composer.AgentSolution(voice, LastDate(messages)), minHours: 1, maxHours: 30));
                resolution = _composer.Resolution(voice, LastDate(messages));
                AddIf(messages, 0.5, MessageAuthor.Customer, () => _composer.CustomerThanks(voice));
                break;

            case TicketState.Unresolved:
                AddQuestionRound(voice, messages, chance: 1, followUpChance: 0.5);
                messages.Add(Next(messages, MessageAuthor.Agent, _composer.AgentUnresolved(voice), minHours: 24, maxHours: 240));
                break;

            default:
                AddIf(messages, 0.4, MessageAuthor.Agent, () => _composer.AgentQuestions(voice, openedOn));
                break;
        }

        return new TicketDraft(voice, Subject: _composer.Subject(voice, openedOn), state, openedAt, messages, resolution, isTestSet, slot.Instance.Code,
            TrapKind: null, DuplicateOf: null, Priority(slot.Instance.Definition.Priority));
    }

    /// <summary>Ticket aperti solo nell'ultimo mese; gli altri ~89% risolti con soluzione, il resto senza.</summary>
    private TicketState ChooseState(DateTimeOffset openedAt)
    {
        if (DateOnly.FromDateTime(openedAt.Date) >= LastDay.AddDays(-30) && _random.Chance(0.5))
        {
            return TicketState.Open;
        }

        return _random.Chance(0.89) ? TicketState.Resolved : TicketState.Unresolved;
    }

    private void AddQuestionRound(TicketVoice voice, List<GeneratedMessage> messages, double chance, double followUpChance = 1)
    {
        if (!_random.Chance(chance))
        {
            return;
        }

        GeneratedMessage question = Next(messages, MessageAuthor.Agent, _composer.AgentQuestions(voice, LastDate(messages)), minHours: 0.3, maxHours: 6);
        messages.Add(question);

        if (_random.Chance(followUpChance))
        {
            messages.Add(Next(messages, MessageAuthor.Customer, _composer.CustomerFollowUp(voice, question.Body, question.SentAt), minHours: 1, maxHours: 26));
        }
    }

    private void AddIf(List<GeneratedMessage> messages, double chance, MessageAuthor author, Func<string> body)
    {
        if (_random.Chance(chance))
        {
            messages.Add(Next(messages, author, body(), minHours: 0.5, maxHours: 20));
        }
    }

    private GeneratedMessage Next(List<GeneratedMessage> messages, MessageAuthor author, string body, double minHours, double maxHours)
    {
        DateTimeOffset sentAt = messages[^1].SentAt.AddMinutes(Math.Round((minHours + _random.NextDouble() * (maxHours - minHours)) * 60));

        return new GeneratedMessage(author, body, author == MessageAuthor.Agent ? OfficeHours(sentAt) : sentAt);
    }

    private static DateOnly LastDate(List<GeneratedMessage> messages) => DateOnly.FromDateTime(messages[^1].SentAt.Date);

    /// <summary>Gli operatori rispondono dal lunedì al venerdì, 8:30–18:30: fuori orario la risposta slitta alla prima ora utile.</summary>
    private DateTimeOffset OfficeHours(DateTimeOffset sentAt)
    {
        DateTime local = TimeZoneInfo.ConvertTime(sentAt, Rome).DateTime;
        bool workingDay = local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

        if (workingDay && local.TimeOfDay >= new TimeSpan(8, 30, 0) && local.TimeOfDay <= new TimeSpan(18, 30, 0))
        {
            return sentAt;
        }

        DateOnly day = DateOnly.FromDateTime(local);
        day = local.TimeOfDay > new TimeSpan(18, 30, 0) || !workingDay ? day.AddDays(1) : day;

        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            day = day.AddDays(1);
        }

        DateTime start = day.ToDateTime(new TimeOnly(8, 30)).AddMinutes(_random.Next(0, 120));

        return new DateTimeOffset(start, Rome.GetUtcOffset(start));
    }

    /// <summary>Lo stesso cliente riapre lo stesso problema dopo pochi giorni: chiuso rimandando al primo ticket.</summary>
    private IEnumerable<TicketDraft> DuplicatesOf(List<TicketDraft> drafts)
    {
        List<TicketDraft> candidates = [.. drafts.Where(d => d.State == TicketState.Resolved && d.OpenedAt < WorkingMoment(LastDay.AddDays(-5)))];

        foreach (TicketDraft original in _random.PickDistinct(candidates, Duplicates))
        {
            DateTimeOffset openedAt = WorkingMoment(DateOnly.FromDateTime(original.OpenedAt.Date).AddDays(_random.Next(1, 4)));
            DateOnly openedOn = DateOnly.FromDateTime(openedAt.Date);
            string symptom = _random.Pick(original.Voice.Case.Definition.IndexedSymptoms);
            List<GeneratedMessage> messages = [new(MessageAuthor.Customer, _composer.FirstCustomerMessage(original.Voice, symptom, openedOn), openedAt)];
            messages.Add(Next(messages, MessageAuthor.Agent, _composer.AgentDuplicate(OriginalNumberPlaceholder), minHours: 0.3, maxHours: 4));

            yield return original with
            {
                Subject = _composer.Subject(original.Voice, openedOn), OpenedAt = openedAt, Messages = messages, Resolution = null, DuplicateOf = original
            };
        }
    }

    private TicketDraft TrapTicket(TrapDefinition trap)
    {
        CustomerProfile customer = _customers.Single(c => c.Definition.EmailDomain == trap.Customer);
        DateOnly day = DateOnly.FromDateTime(trap.OpenedAt.Date);
        string? version = customer.Definition.Version is null ? null : customer.Timeline.Single(p => p.From <= day && p.To >= day).Version;
        CaseInstance? expected = trap.ExpectedCaseCode is null
            ? null
            : _data.Cases.Single(c => c.Code == trap.ExpectedCaseCode && c.Product == customer.Definition.Product);

        TicketVoice voice = Voice(customer, expected ?? _data.Cases.First(c => c.Product == customer.Definition.Product), version);

        return new TicketDraft(voice, trap.Subject, TicketState.Open, trap.OpenedAt, [new(MessageAuthor.Customer, trap.Body, trap.OpenedAt)],
            Resolution: null, IsTestSet: true, trap.ExpectedCaseCode, trap.TrapKind, DuplicateOf: null, TicketPriority.Normal,
            CategoryOverride: expected?.Definition.Category ?? "da-classificare");
    }

    private TicketVoice Voice(CustomerProfile customer, CaseInstance instance, string? version) =>
        new(customer.Definition, customer.VatNumber, _random.Pick(customer.Contacts), _random.Pick(_data.Noise.Operators), instance, version);

    /// <summary>Priorità del caso, con qualche ticket alzato o abbassato di un livello.</summary>
    private TicketPriority Priority(TicketPriority casePriority) =>
        _random.Chance(0.15) ? (TicketPriority)Math.Clamp((int)casePriority + (_random.Chance(0.5) ? 1 : -1), 1, 3) : casePriority;

    /// <summary>Un momento in orario d'ufficio (8:30–18:00, ora di Roma); sabato e domenica slittano al lunedì.</summary>
    private DateTimeOffset WorkingMoment(DateOnly day)
    {
        day = day.DayOfWeek switch { DayOfWeek.Saturday => day.AddDays(2), DayOfWeek.Sunday => day.AddDays(1), _ => day };

        if (day > LastDay)
        {
            day = LastDay.DayOfWeek == DayOfWeek.Saturday ? LastDay.AddDays(-1) : LastDay;
        }

        DateTime local = day.ToDateTime(new TimeOnly(8, 30)).AddMinutes(_random.Next(0, 570));

        return new DateTimeOffset(local, Rome.GetUtcOffset(local));
    }

    private static short Sequence(string product, string version) =>
        DemoCatalog.Product(product).SequenceOf(version);

    /// <summary>Numeri <c>HT-anno-progressivo</c> in ordine di apertura; i duplicati prendono il numero del ticket originale.</summary>
    private static List<GeneratedTicket> Number(List<TicketDraft> drafts)
    {
        List<TicketDraft> ordered = [.. drafts.Select((d, index) => (d, index)).OrderBy(x => x.d.OpenedAt).ThenBy(x => x.index).Select(x => x.d)];
        Dictionary<TicketDraft, string> numbers = new(ReferenceEqualityComparer.Instance);
        Dictionary<int, int> progressives = [];

        foreach (TicketDraft draft in ordered)
        {
            int year = draft.OpenedAt.Year;
            int progressive = progressives[year] = progressives.GetValueOrDefault(year) + 1;
            numbers[draft] = string.Create(CultureInfo.InvariantCulture, $"HT-{year}-{progressive:D4}");
        }

        return [.. ordered.Select(d => d.ToTicket(numbers[d], d.DuplicateOf is null ? null : numbers[d.DuplicateOf]))];
    }

    private sealed record CustomerProfile(CustomerDefinition Definition, IReadOnlyList<VersionPeriod> Timeline, string VatNumber, IReadOnlyList<Contact> Contacts);

    private sealed record CaseWindow(CustomerProfile Customer, VersionPeriod Period);

    private sealed record CaseSlot(CaseInstance Instance, IReadOnlyList<CaseWindow> Windows);

    private sealed record TicketDraft(
        TicketVoice Voice,
        string Subject,
        TicketState State,
        DateTimeOffset OpenedAt,
        IReadOnlyList<GeneratedMessage> Messages,
        GeneratedResolution? Resolution,
        bool IsTestSet,
        string? CaseCode,
        TrapKind? TrapKind,
        TicketDraft? DuplicateOf,
        TicketPriority Priority,
        string? CategoryOverride = null)
    {
        public GeneratedTicket ToTicket(string number, string? duplicateOf)
        {
            List<GeneratedMessage> messages = [.. Messages.Select(m => duplicateOf is null
                ? m
                : m with { Body = m.Body.Replace(OriginalNumberPlaceholder, duplicateOf, StringComparison.Ordinal) })];

            return new GeneratedTicket(
                number, Voice.Customer.EmailDomain, Voice.Customer.Product, Voice.Version, Voice.Customer.Mode,
                CategoryOverride ?? Voice.Case.Definition.Category, Priority, State, Subject, messages[0].Body, OpenedAt,
                State == TicketState.Open ? null : messages[^1].SentAt, IsTestSet, CaseCode, TrapKind, duplicateOf, messages, Resolution);
        }
    }
}
