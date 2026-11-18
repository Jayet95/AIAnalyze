namespace AIAnalyze.Analysis;

public static class Stats
{
    /// <summary>Median of a non-empty sequence. Throws on empty input.</summary>
    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            throw new InvalidOperationException("Median of an empty sequence is undefined.");
        }

        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
