using Dusiburg.AI.Ticket.Data.Seed;
using Dusiburg.AI.Ticket.Shared.Demo;
using Dusiburg.AI.Ticket.Shared.Domain;

namespace Dusiburg.AI.Ticket.DataGen;

/// <summary>Periodo in cui un cliente ha usato una versione.</summary>
internal sealed record VersionPeriod(string Version, short Sequence, DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber + 1;
}

/// <summary>
/// Versioni usate da un cliente nel tempo. SaaS: sempre l'ultima rilasciata. On-premise: ogni versione fino a quella attuale,
/// installata con un ritardo fisso per cliente dopo il rilascio; chi è fermo a una versione vecchia non ha mai visto le successive.
/// </summary>
internal static class CustomerTimeline
{
    public static IReadOnlyList<VersionPeriod> Build(CustomerDefinition customer, Random random, DateOnly firstDay, DateOnly lastDay)
    {
        DemoProduct product = DemoCatalog.Product(customer.Product);
        IReadOnlyList<DemoProductVersion> versions = product.Versions;

        // Versione non registrata: il cliente esiste comunque, la si tratta come l'ultima.
        int current = customer.Version is null ? versions.Count : product.SequenceOf(customer.Version);
        int lagDays = customer.Mode == DeploymentMode.Saas ? 0 : random.Next(20, 121);

        List<(DemoProductVersion Version, short Sequence, DateOnly From)> starts = [.. versions
            .Take(current)
            .Select((v, index) => (v, (short)(index + 1), v.ReleasedOn.AddDays(lagDays)))];

        List<VersionPeriod> periods = [];

        for (int i = 0; i < starts.Count; i++)
        {
            DateOnly from = Max(starts[i].From, firstDay);
            DateOnly to = i + 1 < starts.Count ? starts[i + 1].From.AddDays(-1) : lastDay;

            if (to > lastDay)
            {
                to = lastDay;
            }

            if (from <= to)
            {
                periods.Add(new VersionPeriod(starts[i].Version.Version, starts[i].Sequence, from, to));
            }
        }

        return periods;
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
