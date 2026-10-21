using System.Text.RegularExpressions;
using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Data.Seed;
using Dusiburg.AI.Ticket.Rag.Indexing;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Rag.Tests;

/// <summary>
/// Pulizia e anonimizzazione su tutto lo storico simulato di <c>data/</c> (fase-5.md §2): nessun dato personale residuo nei chunk
/// dei ticket indicizzabili, nessun disclaimer né firma, e il problema descritto dal cliente resta.
/// </summary>
public partial class CorpusCleaningTests
{
    private SeedData _seed = null!;
    private List<(GeneratedTicket Source, Chunk Chunk)> _chunks = null!;

    [OneTimeSetUp]
    public void BuildChunks()
    {
        string directory = SeedData.FindDataDirectory(TestContext.CurrentContext.TestDirectory)
            ?? throw new DirectoryNotFoundException("Cartella data/ del repository non trovata.");
        _seed = SeedData.Load(directory);
        Dictionary<string, string> customers = _seed.Customers.ToDictionary(c => c.EmailDomain, c => c.Name);

        _chunks = [.. _seed.Tickets!.Tickets
            .Select((ticket, index) => (Source: ticket, Entity: ToEntity(ticket, index + 1)))
            .Where(t => ChunkBuilder.IsIndexable(t.Entity))
            .Select(t => (t.Source, ChunkBuilder.ForTicket(t.Entity, customers[t.Source.Customer])))];
    }

    [Test]
    public void Chunks_CoverEveryIndexableTicket()
    {
        //SUT
        Assert.That(_chunks, Has.Count.GreaterThan(600));
    }

    [Test]
    public void Chunks_HaveNoPersonalDataLeft()
    {
        //SUT
        List<string> leaks = [.. _chunks
            .SelectMany(c => PersonalData().Matches(c.Chunk.Text).Select(m => $"{c.Source.Number}: {m.Value}"))];

        Assert.That(leaks, Is.Empty);
    }

    [Test]
    public void Chunks_HaveNoContactNameNorCustomerName()
    {
        //SETUP
        Dictionary<string, string> customers = _seed.Customers.ToDictionary(c => c.EmailDomain, c => c.Name);

        //SUT
        List<string> leaks = [.. _chunks
            .Where(c => c.Chunk.Text.Contains(c.Source.ContactName, StringComparison.OrdinalIgnoreCase)
                || c.Chunk.Text.Contains(customers[c.Source.Customer], StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Source.Number)];

        Assert.That(leaks, Is.Empty);
    }

    [Test]
    public void Chunks_HaveNoDisclaimerSignatureOrQuote()
    {
        //SUT
        List<string> noise = [.. _chunks.Where(c => Noise().IsMatch(c.Chunk.Text)).Select(c => c.Source.Number)];

        Assert.That(noise, Is.Empty);
    }

    [Test]
    public void Chunks_KeepTheErrorCodeOfTheCustomerMessage()
    {
        //SUT
        List<string> lost = [.. _chunks
            .Where(c => ErrorCode().Match(c.Source.Messages[0].Body) is { Success: true } code && !c.Chunk.Text.Contains(code.Value, StringComparison.Ordinal))
            .Select(c => c.Source.Number)];

        Assert.That(lost, Is.Empty);
    }

    private static HistoricalTicket ToEntity(GeneratedTicket ticket, int id)
    {
        var entity = new HistoricalTicket
        {
            Id = id, Number = ticket.Number, ContactName = ticket.ContactName, ContactEmail = ticket.ContactEmail, Category = ticket.Category,
            Subject = ticket.Subject, Description = ticket.Description, State = ticket.State, IsTestSet = ticket.IsTestSet,
            DeploymentMode = ticket.Mode, DuplicateOfId = ticket.DuplicateOf is null ? null : 0,
            Resolution = ticket.Resolution is { } r ? new Resolution { Cause = r.Cause, Solution = r.Solution, IsVerified = r.IsVerified } : null
        };

        entity.Messages.AddRange(ticket.Messages.Select((m, i) => new TicketMessage { Sequence = (short)(i + 1), Author = m.Author, Body = m.Body, SentAt = m.SentAt }));

        return entity;
    }

    [GeneratedRegex(@"@|www\.|\bIT\d{2}[A-Z]\d{10}|_live_|\b\d{11}\b|\b[A-Z]{6}\d{2}[A-Z]\d{2}[A-Z]\d{3}[A-Z]\b|\b3\d{2} \d{6,7}\b|\b0\d{1,3} \d{5,8}\b")]
    private static partial Regex PersonalData();

    [GeneratedRegex(@"riservat|2016/679|prima di stampare|Inviato da|ha scritto:|Messaggio originale|^>|^--$|Tel\.|Cell\.|P\.IVA", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\b(?:FF|DA)-E\d{4}\b")]
    private static partial Regex ErrorCode();
}
