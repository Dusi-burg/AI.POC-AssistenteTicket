using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Shared.Data;
using Dusiburg.AI.Ticket.Shared.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dusiburg.AI.Ticket.Data.Configurations;

internal sealed class AgentRunConfiguration : IEntityTypeConfiguration<AgentRun>
{
    public void Configure(EntityTypeBuilder<AgentRun> builder)
    {
        builder.ToTable(nameof(AgentRun), TicketDbContext.AgentSchema);

        builder.Property(r => r.Trigger).HasColumnName("RunTriggerId").HasConversion<byte>();
        builder.Property(r => r.State).HasColumnName("AgentRunStateId").HasConversion<byte>();
        builder.Property(r => r.TicketNumber).HasMaxLength(ColumnSizes.TicketNumber).IsUnicode(false);
        builder.Property(r => r.TicketTitle).HasMaxLength(ColumnSizes.Title);
        builder.Property(r => r.ErrorCode).HasMaxLength(ColumnSizes.ErrorCode).IsUnicode(false);
        builder.Property(r => r.Error).HasMaxLength(ColumnSizes.Error);
        builder.Property(r => r.TraceId).HasMaxLength(ColumnSizes.TraceId).IsUnicode(false).IsFixedLength();

        // Idempotenza (P2): lo stesso articolo di Zammad non si elabora due volte, qualunque sia la sorgente dell'evento.
        builder.HasIndex(r => new { r.ZammadTicketId, r.ZammadArticleId }).IsUnique();

        // Console: elaborazioni più recenti e per stato.
        builder.HasIndex(r => r.ReceivedAt);

        builder.HasOne<EnumLookup<RunTrigger>>().WithMany().HasForeignKey(r => r.Trigger).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EnumLookup<AgentRunState>>().WithMany().HasForeignKey(r => r.State).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.Draft)
            .WithOne()
            .HasForeignKey<AgentDraft>(d => d.AgentRunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AgentDraftConfiguration : IEntityTypeConfiguration<AgentDraft>
{
    public void Configure(EntityTypeBuilder<AgentDraft> builder)
    {
        builder.ToTable(nameof(AgentDraft), TicketDbContext.AgentSchema);

        builder.Property(d => d.Kind).HasColumnName("DraftKindId").HasConversion<byte>();
        builder.Property(d => d.Priority).HasColumnName("TicketPriorityId").HasConversion<byte?>();
        builder.Property(d => d.Outcome).HasColumnName("DraftOutcomeId").HasConversion<byte>();
        builder.Property(d => d.Category).HasMaxLength(ColumnSizes.Category).IsUnicode(false);
        builder.Property(d => d.Language).HasMaxLength(ColumnSizes.Language).IsUnicode(false);
        builder.Property(d => d.ChatModel).HasMaxLength(ColumnSizes.ModelName).IsUnicode(false);
        builder.Property(d => d.PromptHash).HasMaxLength(ColumnSizes.PromptHash).IsUnicode(false).IsFixedLength();
        builder.Property(d => d.OutcomeReason).HasMaxLength(ColumnSizes.Reason);

        // Liste di stringhe del modello: collezioni primitive, salvate come JSON (nvarchar(max)).
        builder.PrimitiveCollection(d => d.MissingInformation);
        builder.PrimitiveCollection(d => d.Questions);

        // Esiti del feedback (Fase 7): riepilogo per esito e bozze in attesa.
        builder.HasIndex(d => d.Outcome);

        builder.HasOne<EnumLookup<DraftKind>>().WithMany().HasForeignKey(d => d.Kind).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EnumLookup<TicketPriority>>().WithMany().HasForeignKey(d => d.Priority).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EnumLookup<DraftOutcome>>().WithMany().HasForeignKey(d => d.Outcome).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(d => d.Sources)
            .WithOne()
            .HasForeignKey(s => s.AgentDraftId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AgentDraftSourceConfiguration : IEntityTypeConfiguration<AgentDraftSource>
{
    public void Configure(EntityTypeBuilder<AgentDraftSource> builder)
    {
        builder.ToTable(nameof(AgentDraftSource), TicketDbContext.AgentSchema);

        builder.Property(s => s.Source).HasColumnName("ChunkSourceId").HasConversion<byte>();
        builder.Property(s => s.SourceKey).HasMaxLength(ColumnSizes.SourceKey).IsUnicode(false);
        builder.Property(s => s.Reason).HasMaxLength(ColumnSizes.Reason);

        builder.HasIndex(s => new { s.AgentDraftId, s.Rank }).IsUnique();

        // Una reindicizzazione rinnova i chunk: la fonte resta leggibile dalla chiave di business, il riferimento al chunk si perde.
        builder.HasOne<Chunk>().WithMany().HasForeignKey(s => s.ChunkId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<EnumLookup<ChunkSource>>().WithMany().HasForeignKey(s => s.Source).OnDelete(DeleteBehavior.Restrict);
    }
}
