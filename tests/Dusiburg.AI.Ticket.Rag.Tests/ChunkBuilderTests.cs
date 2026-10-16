using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Rag.Indexing;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Rag.Tests;

/// <summary>Dalle fonti ai chunk (fase-5.md §3): fonti escluse, testo dei ticket, sezioni KB, applicabilità delle note.</summary>
public class ChunkBuilderTests
{
    [Test]
    public void IsIndexable_OnlyResolvedTicketsWithOwnSolutionOutsideTheTestSet()
    {
        //SUT
        Assert.That(ChunkBuilder.IsIndexable(Ticket()), Is.True);
        Assert.That(ChunkBuilder.IsIndexable(Ticket(isTestSet: true)), Is.False);
        Assert.That(ChunkBuilder.IsIndexable(Ticket(state: TicketState.Open)), Is.False);
        Assert.That(ChunkBuilder.IsIndexable(Ticket(state: TicketState.Unresolved, withResolution: false)), Is.False);
        Assert.That(ChunkBuilder.IsIndexable(Ticket(duplicateOf: 7)), Is.False);
    }

    [Test]
    public void ForTicket_Text_HasProblemDetailsCauseAndSolutionWithoutPersonalData()
    {
        //SETUP
        HistoricalTicket ticket = Ticket();

        //SUT
        Chunk chunk = ChunkBuilder.ForTicket(ticket, "Studio Contabile Ferretti S.r.l.");

        Assert.That(chunk.Text, Is.EqualTo(
            "Invio bloccato con FF-E2041\n" +
            "Da stamattina tutte le fatture vanno in errore FF-E2041 sul canale.\n" +
            "Dettagli: Usiamo la 5.1.2 sul nostro server.\n\n" +
            "Causa: Suite di cifratura del gateway dismesse.\n" +
            "Soluzione: Abilitato TLS 1.3 in Impostazioni > Trasmissione."));
        Assert.That(chunk.Text, Does.Not.Contain("Ferretti").And.Not.Contain("02 25279401"));
        Assert.That(chunk.Source, Is.EqualTo(ChunkSource.HistoricalTicket));
        Assert.That(chunk.HistoricalTicketId, Is.EqualTo(ticket.Id));
        Assert.That(chunk.DeploymentMode, Is.EqualTo(DeploymentMode.OnPrem));
        Assert.That((chunk.VersionFromId, chunk.VersionToId), Is.EqualTo(((int?)null, (int?)null)));
    }

    [Test]
    public void ForTicket_NotIndexable_Throws()
    {
        //SUT
        Assert.That(() => ChunkBuilder.ForTicket(Ticket(isTestSet: true), "Cliente"), Throws.ArgumentException);
    }

    [Test]
    public void ForKbArticle_OneChunkPerSectionWithTheArticleTitle()
    {
        //SETUP
        var article = new KbArticle
        {
            Id = 3, Code = "KB-FF-0004", ProductId = 1, DeploymentMode = DeploymentMode.OnPrem, VersionFromId = 2, VersionToId = null,
            Title = "Errore FF-E2041", Body = "## Sintomo\n\nL'invio fallisce.\n\n## Soluzione\n\nAbilitare TLS 1.3.", UpdatedOn = new DateOnly(2026, 7, 1)
        };

        //SUT
        List<Chunk> chunks = [.. ChunkBuilder.ForKbArticle(article)];

        Assert.That(chunks.Select(c => c.Text), Is.EqualTo(new[] { "Errore FF-E2041 — Sintomo\nL'invio fallisce.", "Errore FF-E2041 — Soluzione\nAbilitare TLS 1.3." }));
        Assert.That(chunks.Select(c => c.Section), Is.EqualTo(new short[] { 1, 2 }));
        Assert.That(chunks, Has.All.Matches<Chunk>(c => c.KbArticleId == 3 && c.VersionFromId == 2 && c.VersionToId == null && c.DeploymentMode == DeploymentMode.OnPrem));
    }

    [Test]
    public void ForReleaseNote_FixAppliesUpToThePreviousVersion_KnownIssueFromItsOwn()
    {
        //SETUP
        var previous = new ProductVersion { Id = 10, ProductId = 1, Version = "5.0.3", Sequence = 2 };
        var version = new ProductVersion { Id = 11, ProductId = 1, Version = "5.1.2", Sequence = 3 };

        //SUT
        Chunk fix = ChunkBuilder.ForReleaseNote(Note(ReleaseNoteType.Fix), version, previous, "FattureFlow");
        Chunk issue = ChunkBuilder.ForReleaseNote(Note(ReleaseNoteType.KnownIssue), version, previous, "FattureFlow");
        Chunk feature = ChunkBuilder.ForReleaseNote(Note(ReleaseNoteType.Feature), version, previous, "FattureFlow");

        Assert.That((fix.VersionFromId, fix.VersionToId), Is.EqualTo(((int?)null, (int?)10)));
        Assert.That((issue.VersionFromId, issue.VersionToId), Is.EqualTo(((int?)11, (int?)null)));
        Assert.That((feature.VersionFromId, feature.VersionToId), Is.EqualTo(((int?)11, (int?)null)));
        Assert.That(fix.Text, Is.EqualTo("FattureFlow 5.1.2 — Correzione: TLS 1.3\nIl canale negozia TLS 1.3."));
        Assert.That(fix.DeploymentMode, Is.Null);
    }

    private static ReleaseNote Note(ReleaseNoteType type) => new()
    {
        Id = 5, Code = "RN-FF-5.1.2-01", ProductVersionId = 11, Type = type, Title = "TLS 1.3", Body = "Il canale negozia TLS 1.3."
    };

    private static HistoricalTicket Ticket(
        bool isTestSet = false, TicketState state = TicketState.Resolved, bool withResolution = true, int? duplicateOf = null)
    {
        var ticket = new HistoricalTicket
        {
            Id = 42, Number = "HT-2026-0042", CustomerId = 1, ContactName = "Giorgio Ferretti", ContactEmail = "giorgio.ferretti@ferretti.example.it",
            ProductId = 1, ProductVersionId = 11, DeploymentMode = DeploymentMode.OnPrem, Category = "trasmissione", Priority = TicketPriority.High,
            State = state, Subject = "Invio bloccato con FF-E2041", Description = "-", OpenedAt = DateTimeOffset.UnixEpoch, IsTestSet = isTestSet,
            DuplicateOfId = duplicateOf,
            Resolution = withResolution ? new Resolution { Cause = "Suite di cifratura del gateway dismesse.", Solution = "Abilitato TLS 1.3 in Impostazioni > Trasmissione." } : null
        };

        ticket.Messages.AddRange(
        [
            Message(1, MessageAuthor.Customer, "Buongiorno,\nDa stamattina tutte le fatture vanno in errore FF-E2041 sul canale.\n\nGiorgio Ferretti\nTel. 02 25279401"),
            Message(2, MessageAuthor.Agent, "Gentile Giorgio, quale versione usate?"),
            Message(3, MessageAuthor.Customer, "Usiamo la 5.1.2 sul nostro server.\n\nGiorgio Ferretti\nStudio Contabile Ferretti S.r.l."),
            Message(4, MessageAuthor.Agent, "Abiliti TLS 1.3 in Impostazioni > Trasmissione."),
            Message(5, MessageAuthor.Customer, "Perfetto, ora funziona. Grazie!")
        ]);

        return ticket;
    }

    private static TicketMessage Message(short sequence, MessageAuthor author, string body) =>
        new() { Sequence = sequence, Author = author, Body = body, SentAt = DateTimeOffset.UnixEpoch.AddHours(sequence) };
}
