using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.Shared.Catalog;

// Contratti di /api/catalog: i dati demo del "gestionale" simulato, in sola lettura per la pagina "Dati demo" della console.

public sealed record VersionView(string Version, DateOnly ReleasedOn, DateOnly? EndOfSupportOn);

public sealed record ProductView(string Code, string Name, IReadOnlyList<VersionView> Versions);

public sealed record CustomerView(string Name, string EmailDomain, string ProductCode, string? Version, DeploymentMode Mode, SlaLevel Sla, bool InZammad);

/// <param name="VersionFrom">Prima versione a cui si applica; <c>null</c> = da sempre.</param>
/// <param name="VersionTo">Ultima versione a cui si applica; <c>null</c> = ancora valido.</param>
public sealed record KbArticleView(string Code, string ProductCode, DeploymentMode? Mode, string? VersionFrom, string? VersionTo, string Title, string Body, DateOnly UpdatedOn);

public sealed record ReleaseNoteView(string Code, string ProductCode, string Version, ReleaseNoteType Type, string Title, string Body);

public sealed record CatalogView(
    IReadOnlyList<ProductView> Products,
    IReadOnlyList<CustomerView> Customers,
    IReadOnlyList<KbArticleView> KbArticles,
    IReadOnlyList<ReleaseNoteView> ReleaseNotes);
