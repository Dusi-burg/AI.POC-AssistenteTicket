using Dusiburg.AI.Ticket.Shared.Data;
using Dusiburg.AI.Ticket.Shared.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dusiburg.AI.Ticket.Data.Configurations;

// Convenzioni (skill db-operations): tabelle al singolare, PK numerica "Id", FK qualificate (<Entity>Id), niente PK GUID,
// chiavi di business stringa come colonne univoche, enum come FK verso tabelle di lookup tinyint generate dal codice.
// Codici e chiavi in varchar, testi liberi in nvarchar.

/// <summary>Tabella di lookup di un enum nello schema del suo proprietario: PK tinyint = valore esplicito del membro, righe da <c>Enum.GetValues</c>.</summary>
internal abstract class EnumLookupConfiguration<TEnum>(string schema) : IEntityTypeConfiguration<EnumLookup<TEnum>>
    where TEnum : struct, Enum
{
    public void Configure(EntityTypeBuilder<EnumLookup<TEnum>> builder)
    {
        builder.ToTable(typeof(TEnum).Name, schema);

        builder.HasKey(l => l.Id);

        builder.Property(l => l.Id).HasConversion<byte>().ValueGeneratedNever();
        builder.Property(l => l.Name).HasMaxLength(ColumnSizes.LookupName).IsUnicode(false);

        builder.HasIndex(l => l.Name).IsUnique();

        builder.HasData(Enum.GetValues<TEnum>().Select(value => new EnumLookup<TEnum> { Id = value, Name = value.ToString() }));
    }
}

internal sealed class DeploymentModeConfiguration() : EnumLookupConfiguration<DeploymentMode>(TicketDbContext.SupportSchema);

internal sealed class SlaLevelConfiguration() : EnumLookupConfiguration<SlaLevel>(TicketDbContext.SupportSchema);

internal sealed class TicketPriorityConfiguration() : EnumLookupConfiguration<TicketPriority>(TicketDbContext.SupportSchema);

internal sealed class TicketStateConfiguration() : EnumLookupConfiguration<TicketState>(TicketDbContext.SupportSchema);

internal sealed class MessageAuthorConfiguration() : EnumLookupConfiguration<MessageAuthor>(TicketDbContext.SupportSchema);

internal sealed class ReleaseNoteTypeConfiguration() : EnumLookupConfiguration<ReleaseNoteType>(TicketDbContext.SupportSchema);

internal sealed class TrapKindConfiguration() : EnumLookupConfiguration<TrapKind>(TicketDbContext.SupportSchema);

internal sealed class ChunkSourceConfiguration() : EnumLookupConfiguration<ChunkSource>(TicketDbContext.RagSchema);

internal sealed class RunTriggerConfiguration() : EnumLookupConfiguration<RunTrigger>(TicketDbContext.AgentSchema);

internal sealed class AgentRunStateConfiguration() : EnumLookupConfiguration<AgentRunState>(TicketDbContext.AgentSchema);

internal sealed class DraftKindConfiguration() : EnumLookupConfiguration<DraftKind>(TicketDbContext.AgentSchema);

internal sealed class DraftOutcomeConfiguration() : EnumLookupConfiguration<DraftOutcome>(TicketDbContext.AgentSchema);

/// <summary>Lunghezze delle colonne: le usa anche chi controlla un valore prima di scriverlo (seed, importazioni).</summary>
public static class ColumnSizes
{
    public const int LookupName = 50;
    public const int ProductCode = 10;
    public const int ProductName = 100;
    public const int Version = 20;
    public const int EmailDomain = 100;
    public const int CustomerName = 200;
    public const int TicketNumber = 20;
    public const int Category = 50;
    public const int CaseCode = 40;
    public const int Subject = 200;
    public const int Cause = 2000;
    public const int KbCode = 20;
    public const int ReleaseNoteCode = 30;
    public const int Title = 200;

    /// <summary>La più lunga fra le chiavi di business citabili (ticket, articolo, nota).</summary>
    public const int SourceKey = ReleaseNoteCode;

    public const int Reason = 1000;
    public const int Language = 10;
    public const int ModelName = 100;
    public const int PromptHash = 64;
    public const int ErrorCode = 50;
    public const int Error = 2000;
    public const int TraceId = 32;
}
