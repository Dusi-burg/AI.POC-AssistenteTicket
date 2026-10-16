using Dusiburg.AI.Ticket.Data.Configurations;
using Dusiburg.AI.Ticket.Data.Entities;
using Dusiburg.AI.Ticket.Shared.Data;
using Dusiburg.AI.Ticket.Shared.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Dusiburg.AI.Ticket.Data;

/// <summary>
/// Database Ticket: schemi <c>support</c>, <c>rag</c> e <c>agent</c> in un solo contesto, perché l'Agent è l'unico proprietario e
/// così le FK fra schemi sono vere. Lo schema si crea da zero dal modello, senza migration (G4); la dimensione della colonna
/// <c>VECTOR</c> è un parametro del modello (<see cref="TicketDbContextOptions.EmbeddingDimensions"/>).
/// </summary>
public sealed class TicketDbContext(DbContextOptions<TicketDbContext> options, TicketDbContextOptions model) : DbContext(options)
{
    public const string SupportSchema = "support";
    public const string RagSchema = "rag";
    public const string AgentSchema = "agent";

    public int EmbeddingDimensions => model.EmbeddingDimensions;

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductVersion> ProductVersions => Set<ProductVersion>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<HistoricalTicket> HistoricalTickets => Set<HistoricalTicket>();

    public DbSet<TicketMessage> TicketMessages => Set<TicketMessage>();

    public DbSet<Resolution> Resolutions => Set<Resolution>();

    public DbSet<KbArticle> KbArticles => Set<KbArticle>();

    public DbSet<ReleaseNote> ReleaseNotes => Set<ReleaseNote>();

    public DbSet<Chunk> Chunks => Set<Chunk>();

    public DbSet<EmbeddingInfo> EmbeddingInfo => Set<EmbeddingInfo>();

    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();

    public DbSet<AgentDraft> AgentDrafts => Set<AgentDraft>();

    public DbSet<AgentDraftSource> AgentDraftSources => Set<AgentDraftSource>();

    public DbSet<EnumLookup<DeploymentMode>> DeploymentModes => Set<EnumLookup<DeploymentMode>>();

    public DbSet<EnumLookup<TicketState>> TicketStates => Set<EnumLookup<TicketState>>();

    public DbSet<EnumLookup<ChunkSource>> ChunkSources => Set<EnumLookup<ChunkSource>>();

    public DbSet<EnumLookup<AgentRunState>> AgentRunStates => Set<EnumLookup<AgentRunState>>();

    public DbSet<EnumLookup<DraftOutcome>> DraftOutcomes => Set<EnumLookup<DraftOutcome>>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        // Il modello dipende dalla dimensione del vettore: la chiave della cache la include.
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, TicketModelCacheKeyFactory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ChunkConfiguration ha un parametro (la dimensione del vettore): si esclude dalla scansione e si applica a mano.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TicketDbContext).Assembly, type => type != typeof(ChunkConfiguration));
        modelBuilder.ApplyConfiguration(new ChunkConfiguration(EmbeddingDimensions));
    }
}

/// <summary>Parametri del modello che non vengono dalla connection string.</summary>
public sealed record TicketDbContextOptions(int EmbeddingDimensions);

/// <summary>Un modello per dimensione del vettore: nello stesso processo convivono il database della demo e quelli dei test.</summary>
internal sealed class TicketModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is TicketDbContext ticket
            ? (context.GetType(), ticket.EmbeddingDimensions, designTime)
            : (context.GetType(), designTime);
}
