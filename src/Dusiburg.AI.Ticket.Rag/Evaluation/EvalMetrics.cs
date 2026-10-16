namespace Dusiburg.AI.Ticket.Rag.Evaluation;

/// <summary>Metriche del retrieval a livello di fonte, pure (come <c>EvalMetrics</c> di Sinistri).</summary>
public static class EvalMetrics
{
    /// <summary>Posizione (da 1) della prima fonte rilevante entro le prime <paramref name="maxRank"/>; null se nessuna.</summary>
    public static int? FirstRelevantRank(IReadOnlyCollection<string> relevant, IReadOnlyList<string> ranked, int maxRank = 10)
    {
        for (int i = 0; i < Math.Min(maxRank, ranked.Count); i++)
        {
            if (relevant.Contains(ranked[i]))
            {
                return i + 1;
            }
        }

        return null;
    }

    /// <summary>Almeno una fonte rilevante fra le prime <paramref name="k"/>.</summary>
    public static bool HitAtK(IReadOnlyCollection<string> relevant, IReadOnlyList<string> ranked, int k) =>
        FirstRelevantRank(relevant, ranked, k) is not null;

    /// <summary>1 / rank della prima rilevante, 0 se nessuna nelle prime <paramref name="maxRank"/>.</summary>
    public static double ReciprocalRank(IReadOnlyCollection<string> relevant, IReadOnlyList<string> ranked, int maxRank = 10) =>
        FirstRelevantRank(relevant, ranked, maxRank) is { } rank ? 1.0 / rank : 0;

    /// <summary>Media; 0 per un insieme vuoto.</summary>
    public static double Mean(IEnumerable<double> values)
    {
        double[] array = [.. values];

        return array.Length == 0 ? 0 : array.Average();
    }

    /// <summary>Percentile con il metodo nearest-rank; 0 per un insieme vuoto.</summary>
    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = [.. values.Order()];

        return sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(percentile / 100 * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}
