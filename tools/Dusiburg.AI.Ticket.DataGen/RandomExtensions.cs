namespace Dusiburg.AI.Ticket.DataGen;

/// <summary>Estrazioni su <see cref="Random"/> con seed: a parità di seed e di ordine delle chiamate il risultato è identico.</summary>
internal static class RandomExtensions
{
    public static T Pick<T>(this Random random, IReadOnlyList<T> items) =>
        items.Count > 0 ? items[random.Next(items.Count)] : throw new ArgumentException("Elenco vuoto.", nameof(items));

    public static bool Chance(this Random random, double probability) => random.NextDouble() < probability;

    /// <summary>Fino a <paramref name="count"/> elementi distinti, nell'ordine dell'elenco.</summary>
    public static IReadOnlyList<T> PickDistinct<T>(this Random random, IReadOnlyList<T> items, int count)
    {
        List<int> indexes = [.. Enumerable.Range(0, items.Count)];
        List<int> chosen = [];

        for (int i = 0; i < Math.Min(count, items.Count); i++)
        {
            int position = random.Next(indexes.Count);
            chosen.Add(indexes[position]);
            indexes.RemoveAt(position);
        }

        return [.. chosen.Order().Select(index => items[index])];
    }

    public static T PickWeighted<T>(this Random random, IReadOnlyList<T> items, Func<T, double> weight)
    {
        double total = items.Sum(weight);
        double target = random.NextDouble() * total;

        foreach (T item in items)
        {
            target -= weight(item);

            if (target < 0)
            {
                return item;
            }
        }

        return items[^1];
    }

    public static string Digits(this Random random, int count) =>
        string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + random.Next(10))));
}
