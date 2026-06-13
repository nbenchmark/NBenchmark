namespace NBenchmark.Stats;

internal static class MultipleComparisons
{
    public static double[] HolmBonferroni(IReadOnlyList<double> rawPValues)
    {
        var m = rawPValues.Count;
        var adjusted = new double[m];

        if (m == 0)
            return adjusted;

        var testableCount = 0;

        for (var i = 0; i < m; i++)
        {
            if (double.IsNaN(rawPValues[i]))
                adjusted[i] = double.NaN;
            else
                testableCount++;
        }

        if (testableCount == 0)
            return adjusted;

        var indices = new int[testableCount];
        var write = 0;

        for (var i = 0; i < m; i++)
        {
            if (!double.IsNaN(rawPValues[i]))
                indices[write++] = i;
        }

        Array.Sort(indices, (a, b) => rawPValues[a].CompareTo(rawPValues[b]));

        var minAdjusted = 0.0;

        for (var j = 0; j < testableCount; j++)
        {
            var idx = indices[j];
            var step = (testableCount - j) * rawPValues[idx];
            var adj = Math.Max(step, minAdjusted);
            adj = Math.Min(adj, 1.0);
            adjusted[idx] = adj;
            minAdjusted = adj;
        }

        return adjusted;
    }
}
