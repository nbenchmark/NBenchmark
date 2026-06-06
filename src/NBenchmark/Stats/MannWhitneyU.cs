namespace NBenchmark.Stats;

public static class MannWhitneyU
{
    public static double Test(double[] sampleA, double[] sampleB)
    {
        var n1 = sampleA.Length;
        var n2 = sampleB.Length;

        if (n1 == 0 || n2 == 0) return double.NaN;
        if (n1 < 5 || n2 < 5) return double.NaN;

        var combined = new (double Value, int Group)[n1 + n2];
        for (var i = 0; i < n1; i++) combined[i]     = (sampleA[i], 0);
        for (var i = 0; i < n2; i++) combined[n1 + i] = (sampleB[i], 1);

        Array.Sort(combined, (a, b) => a.Value.CompareTo(b.Value));

        var ranks = new double[n1 + n2];
        var j     = 0;
        while (j < combined.Length)
        {
            var k = j + 1;
            while (k < combined.Length && combined[k].Value == combined[j].Value)
                k++;

            var rankCount = k - j;
            var meanRank  = (j + k + 1) / 2.0;
            for (var t = j; t < k; t++)
                ranks[t] = meanRank;

            j = k;
        }

        double R1 = 0;
        for (var i = 0; i < combined.Length; i++)
            if (combined[i].Group == 0)
                R1 += ranks[i];

        var U1 = R1 - (double)n1 * (n1 + 1) / 2.0;
        var U2 = (double)n1 * n2 - U1;
        var U  = Math.Min(U1, U2);

        var mu    = (double)n1 * n2 / 2.0;
        var total = n1 + n2;

        var tieCorrection = 0.0;
        j = 0;
        while (j < combined.Length)
        {
            var k = j + 1;
            while (k < combined.Length && combined[k].Value == combined[j].Value)
                k++;

            var t = k - j;
            if (t > 1)
                tieCorrection += t * t * t - t;

            j = k;
        }

        var sigma = Math.Sqrt(
            ((double)n1 * n2 / (total * (total - 1))) *
            ((total * total * total - total) / 12.0 - tieCorrection / 12.0)
        );

        if (sigma == 0) return 1.0;

        var z = (U - mu) / sigma;

        return 2.0 * (1.0 - NormalCdf(Math.Abs(z)));
    }

    private static double NormalCdf(double x)
    {
        const double a1 =  0.254829592;
        const double a2 = -0.284496736;
        const double a3 =  1.421413741;
        const double a4 = -1.453152027;
        const double a5 =  1.061405429;
        const double p  =  0.3275911;

        var sign = x < 0 ? -1.0 : 1.0;
        x = Math.Abs(x) / Math.Sqrt(2.0);

        var t = 1.0 / (1.0 + p * x);
        var y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);

        return 0.5 * (1.0 + sign * y);
    }
}
