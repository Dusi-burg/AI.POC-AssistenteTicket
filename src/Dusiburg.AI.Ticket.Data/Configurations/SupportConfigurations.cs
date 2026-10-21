using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Shared.Data;
using Dusiburg.AI.Ticket.Shared.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dusiburg.AI.Ticket.Data.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable(nameof(Product), TicketDbContext.SupportSchema);

        builder.Property(p => p.Code).HasMaxLength(ColumnSizes.ProductCode).IsUnicode(false);
        builder.Property(p => p.Name).HasMaxLength(ColumnSizes.ProductName);

        builder.HasIndex(p => p.Code).IsUnique();
    }
}

internal sealed class ProductVersionConfiguration : IEntityTypeConfiguration<ProductVersion>
{
    public void Configure(EntityTypeBuilder<ProductVersion> builder)
    {
        builder.ToTable(nameof(ProductVersion), TicketDbContext.SupportSchema);

        builder.Property(v => v.Version).HasMaxLength(ColumnSizes.Version).IsUnicode(false);

        builder.HasIndex(v => new { v.ProductId, v.Version }).IsUnique();
        builder.HasIndex(v => new { v.ProductId, v.Sequence }).IsUnique();

        builder.HasOne(v => v.Product)
            .WithMany(p => p.Versions)
            .HasForeignKey(v => v.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable(nameof(Customer), TicketDbContext.SupportSchema);

        builder.Property(c => c.EmailDomain).HasMaxLength(ColumnSizes.EmailDomain).IsUnicode(false);
        builder.Property(c => c.Name).HasMaxLength(ColumnSizes.CustomerName);
        builder.Property(c => c.DeploymentMode).HasColumnName("DeploymentModeId").HasConversion<byte>();
        builder.Property(c => c.SlaLevel).HasColumnName("SlaLevelId").HasConversion<byte>();

        builder.HasIndex(c => c.EmailDomain).IsUnique();

        // Univoco solo dove valorizzato: EF aggiunge il filtro IS NOT NULL sulle colonne annullabili.
        builder.HasIndex(c => c.ZammadOrganizationId).IsUnique();

        builder.HasOne<Product>().WithMany().HasForeignKey(c => c.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductVersion>().WithMany().HasForeignKey(c => c.ProductVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EnumLookup<DeploymentMode>>().WithMany().HasForeignKey(c => c.DeploymentMode).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EnumLookup<SlaLevel>>().WithMany().HasForeignKey(c => c.SlaLevel).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class HistoricalTicketConfiguration : IEntityTypeConfiguration<HistoricalTicket>
{
    public void Configure(EntityTypeBuilder<HistoricalTicket> builder)
    {
        builder.ToTable(nameof(HistoricalTicket), TicketDbContext.SupportSchema);

        builder.Property(t => t.Number).HasMaxLength(ColumnSizes.TicketNumber).IsUnicode(false);
        builder.Property(t => t.ContactName).HasMaxLength(ColumnSizes.ContactName);
        builder.Property(t => t.ContactEmail).HasMaxLength(ColumnSizes.ContactEmail).IsUnicode(false);
        builder.Property(t => t.Category).HasMaxLength(ColumnSizes.Category).IsUnicode(false);
        builder.Property(t => t.Subject).HasMaxLength(ColumnSizes.Subject);
        builder.Property(t => t.DeploymentMode).HasColumnName("DeploymentModeId").HasConversion<byte>();
        builder.Property(t => t.Priority).HasColumnName("TicketPriorityId").HasConversion<byte>();
        builder.Property(t => t.State).HasColumnName("TicketStateId").HasConversion<byte>();
        builder.Property(t => t.CaseCode).HasMaxLength(ColumnSizes.CaseCode).IsUnicode(false);
        builder.Property(t => t.TrapKind).HasColumnName("TrapKindId").HasConversion<byte?>();

        builder.HasIndex(t => t.Number).IsUnique();

        // Verità di riferimento della valutazione: fonti dello stesso caso.
        builder.HasIndex(t => t.CaseCode);
        builder.HasIndex(t => t.ZammadTicketId).IsUnique();

        // L'indicizzazione legge i ticket fuori dal test set, la valutazione quelli dentro.
        builder.HasIndex(t => new { t.IsTestSet, t.State });

        builder.HasOne<Customer>().WithMany().HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Product>().WithMany().HasForeignKey(t => t.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductVersion>().WithMany().HasForeignKey(t => t.ProductVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EnumLookup<DeploymentMode>>().WithMany().HasForeignKey(t => t.DeploymentMode).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EnumLookup<TicketPriority>>().WithMany().HasForeignKey(t => t.Priority).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EnumLookup<TicketState>>().WithMany().HasForeignKey(t => t.State).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EnumLookup<TrapKind>>().WithMany().HasForeignKey(t => t.TrapKind).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<HistoricalTicket>().WithMany().HasForeignKey(t => t.DuplicateOfId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(t => t.Messages)
            .WithOne()
            .HasForeignKey(m => m.HistoricalTicketId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(t => t.Resolution)
            .WithOne()
            .HasForeignKey<Resolution>(r => r.HistoricalTicketId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TicketMessageConfiguration : IEntityTypeConfiguration<TicketMessage>
{
    public void Configure(EntityTypeBuilder<TicketMessage> builder)
    {
        builder.ToTable(nameof(TicketMessage), TicketDbContext.SupportSchema);

        builder.Property(m => m.Author).HasColumnName("MessageAuthorId").HasConversion<byte>();

        builder.HasIndex(m => new { m.HistoricalTicketId, m.Sequence }).IsUnique();

        builder.HasOne<EnumLookup<MessageAuthor>>().WithMany().HasForeignKey(m => m.Author).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ResolutionConfiguration : IEntityTypeConfiguration<Resolution>
{
    public void Configure(EntityTypeBuilder<Resolution> builder)
    {
        builder.ToTable(nameof(Resolution), TicketDbContext.SupportSchema);

        builder.Property(r => r.Cause).HasMaxLength(ColumnSizes.Cause);
    }
}

internal sealed class KbArticleConfiguration : IEntityTypeConfiguration<KbArticle>
{
    public void Configure(EntityTypeBuilder<KbArticle> builder)
    {
        builder.ToTable(nameof(KbArticle), TicketDbContext.SupportSchema);

        builder.Property(a => a.Code).HasMaxLength(ColumnSizes.KbCode).IsUnicode(false);
        builder.Property(a => a.Title).HasMaxLength(ColumnSizes.Title);
        builder.Property(a => a.DeploymentMode).HasColumnName("DeploymentModeId").HasConversion<byte?>();

        builder.HasIndex(a => a.Code).IsUnique();

        builder.HasOne<Product>().WithMany().HasForeignKey(a => a.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductVersion>().WithMany().HasForeignKey(a => a.VersionFromId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductVersion>().WithMany().HasForeignKey(a => a.VersionToId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EnumLookup<DeploymentMode>>().WithMany().HasForeignKey(a => a.DeploymentMode).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ReleaseNoteConfiguration : IEntityTypeConfiguration<ReleaseNote>
{
    public void Configure(EntityTypeBuilder<ReleaseNote> builder)
    {
        builder.ToTable(nameof(ReleaseNote), TicketDbContext.SupportSchema);

        builder.Property(n => n.Code).HasMaxLength(ColumnSizes.ReleaseNoteCode).IsUnicode(false);
        builder.Property(n => n.Title).HasMaxLength(ColumnSizes.Title);
        builder.Property(n => n.Type).HasColumnName("ReleaseNoteTypeId").HasConversion<byte>();

        builder.HasIndex(n => n.Code).IsUnique();

        builder.HasOne<ProductVersion>().WithMany().HasForeignKey(n => n.ProductVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EnumLookup<ReleaseNoteType>>().WithMany().HasForeignKey(n => n.Type).OnDelete(DeleteBehavior.Restrict);
    }
}
