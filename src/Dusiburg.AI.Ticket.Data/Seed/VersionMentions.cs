using System.Text.RegularExpressions;
using Dusiburg.AI.Ticket.Shared.Demo;

namespace Dusiburg.AI.Ticket.Data.Seed;

/// <summary>
/// Versioni citate in un testo (<c>x.y.z</c>): un messaggio del 2024 non può parlare della 5.2.1. Le stringhe che non sono
/// versioni del prodotto si ignorano.
/// </summary>
public static partial class VersionMentions
{
    public static IEnumerable<string> In(string text) => VersionPattern().Matches(text).Select(m => m.Value);

    /// <summary>Il testo cita una versione del prodotto rilasciata dopo <paramref name="date"/>.</summary>
    public static bool IsAnachronistic(string text, string productCode, DateOnly date)
    {
        IReadOnlyList<DemoProductVersion> versions = DemoCatalog.Product(productCode).Versions;

        return In(text).Any(mention => versions.SingleOrDefault(v => v.Version == mention) is { } version && version.ReleasedOn > date);
    }

    [GeneratedRegex(@"\b\d+\.\d+\.\d+\b")]
    private static partial Regex VersionPattern();
}
