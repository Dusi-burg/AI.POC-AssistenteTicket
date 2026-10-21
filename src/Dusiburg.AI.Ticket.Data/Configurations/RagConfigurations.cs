using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Shared.Data;
using Dusiburg.AI.Ticket.Shared.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dusiburg.AI.Ticket.Data.Configurations;

/// <summary>
/// La dimensione del vettore viene dalla configurazione (<c>EMBEDDING_DIMENSIONS</c>), mai fissa nel codice: per questo la
/// configurazione ha un parametro e il contesto la applica a mano. Non implementa <see cref="IEntityTypeConfiguration{TEntity}"/>:
/// la scansione dell'assembly di EF Core 10 segnala con un avviso ogni configurazione senza costruttore vuoto, anche se esclusa
/// dal filtro.
/// </summary>
internal sealed class ChunkConfiguration(int embeddingDimensions)
{
    public void Configure(EntityTypeBuilder<Chunk> builder)
    {
        builder.ToTable(nameof(Chunk), TicketDbContext.RagSchema, table => table.HasCheckConstraint("CK_Chunk_Source", SourceConstraint()));

        builder.Property(c => c.Source).HasColumnName("ChunkSourceId").HasConversion<byte>();
        builder.Property(c => c.DeploymentMode).HasColumnName("DeploymentModeId").HasConversion<byte?>();
        builder.Property(c => c.Embedding).HasColumnType($"vector({embeddingDimensions})");

        // Un frammento per (fonte, sezione); EF filtra gli indici univoci sulle colonne annullabili (IS NOT NULL).
        builder.HasIndex(c => new { c.HistoricalTicketId, c.Section }).IsUnique();
        builder.HasIndex(c => new { c.KbArticleId, c.Section }).IsUnique();
        builder.HasIndex(c => new { c.ReleaseNoteId, c.Section }).IsUnique();

        builder.HasOne<HistoricalTicket>().WithMany().HasForeignKey(c => c.HistoricalTicketId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<KbArticle>().WithMany().HasForeignKey(c => c.KbArticleId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ReleaseNote>().WithMany().HasForeignKey(c => c.ReleaseNoteId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Product>().WithMany().HasForeignKey(c => c.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductVersion>().WithMany().HasForeignKey(c => c.VersionFromId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductVersion>().WithMany().HasForeignKey(c => c.VersionToId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EnumLookup<ChunkSource>>().WithMany().HasForeignKey(c => c.Source).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EnumLookup<DeploymentMode>>().WithMany().HasForeignKey(c => c.DeploymentMode).OnDelete(DeleteBehavior.Restrict);
    }

    /// <summary>È valorizzata esattamente la FK della fonte indicata da <c>ChunkSourceId</c>.</summary>
    private static string SourceConstraint()
    {
        (ChunkSource Source, string Column)[] sources =
        [
            (ChunkSource.HistoricalTicket, nameof(Chunk.HistoricalTicketId)),
            (ChunkSource.KbArticle, nameof(Chunk.KbArticleId)),
            (ChunkSource.ReleaseNote, nameof(Chunk.ReleaseNoteId)),
        ];

        return string.Join(" OR ", sources.Select(source =>
            $"([ChunkSourceId] = {(byte)source.Source} AND " +
            string.Join(" AND ", sources.Select(other => $"[{other.Column}] IS {(other.Source == source.Source ? "NOT NULL" : "NULL")}")) +
            ")"));
    }
}

internal sealed class EmbeddingInfoConfiguration : IEntityTypeConfiguration<EmbeddingInfo>
{
    public void Configure(EntityTypeBuilder<EmbeddingInfo> builder)
    {
        builder.ToTable(nameof(EmbeddingInfo), TicketDbContext.RagSchema,
            table => table.HasCheckConstraint("CK_EmbeddingInfo_Singleton", $"[Id] = {EmbeddingInfo.SingletonId}"));

        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Model).HasMaxLength(ColumnSizes.ModelName).IsUnicode(false);
    }
}
