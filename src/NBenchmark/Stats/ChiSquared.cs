namespace NBenchmark.Stats;

/// <summary>
///     Chi-squared distribution helpers. Only the survival function (upper tail) is needed,
///     to turn a Kruskal-Wallis <c>H</c> statistic into a p-value. It is evaluated through
///     the regularized upper incomplete gamma function <c>Q(df/2, x/2)</c> using the series
///     and continued-fraction expansions from <i>Numerical Recipes</i>, which are accurate to
///     roughly 1e-12 across the degrees of freedom and statistic magnitudes seen here.
/// </summary>
public static class ChiSquared
{
    private const double Epsilon = 1e-14;
    private const int MaxIterations = 300;
    private const double TinyFloor = 1e-300;

    // Lanczos approximation coefficients (g = 7, n = 9).
    private static readonly double[] LanczosCoefficients =
    [
        0.99999999999980993,
        676.5203681218851,
        -1259.1392167224028,
        771.32342877765313,
        -176.61502916214059,
        12.507343278686905,
        -0.13857109526572012,
        9.9843695780195716e-6,
        1.5056327351493116e-7,
    ];

    /// <summary>
    ///     The survival function <c>P(X &gt; x)</c> for a chi-squared distribution with
    ///     <paramref name="degreesOfFreedom" /> degrees of freedom. Returns 1 for
    ///     non-positive <paramref name="x" />.
    /// </summary>
    public static double SurvivalFunction(double x, int degreesOfFreedom)
    {
        if (degreesOfFreedom < 1)
            return double.NaN;

        if (x <= 0)
            return 1.0;

        return RegularizedGammaUpper(degreesOfFreedom / 2.0, x / 2.0);
    }

    /// <summary>Natural log of the gamma function via the Lanczos approximation.</summary>
    internal static double LogGamma(double x)
    {
        if (x < 0.5)
            return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1.0 - x);

        x -= 1.0;
        var a = LanczosCoefficients[0];
        var t = x + 7.5;

        for (var i = 1; i < LanczosCoefficients.Length; i++)
        {
            a += LanczosCoefficients[i] / (x + i);
        }

        return 0.5 * Math.Log(2.0 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
    }

    /// <summary>Regularized upper incomplete gamma function <c>Q(a, x) = 1 − P(a, x)</c>.</summary>
    private static double RegularizedGammaUpper(double a, double x)
    {
        if (x < 0 || a <= 0)
            return double.NaN;

        if (x == 0)
            return 1.0;

        return x < a + 1.0
            ? 1.0 - GammaSeries(a, x)
            : GammaContinuedFraction(a, x);
    }

    /// <summary>Series expansion for the regularized lower incomplete gamma <c>P(a, x)</c>.</summary>
    private static double GammaSeries(double a, double x)
    {
        var ap = a;
        var sum = 1.0 / a;
        var del = sum;

        for (var n = 0; n < MaxIterations; n++)
        {
            ap += 1.0;
            del *= x / ap;
            sum += del;

            if (Math.Abs(del) < Math.Abs(sum) * Epsilon)
                break;
        }

        return sum * Math.Exp(-x + a * Math.Log(x) - LogGamma(a));
    }

    /// <summary>Continued-fraction expansion for the regularized upper incomplete gamma <c>Q(a, x)</c>.</summary>
    private static double GammaContinuedFraction(double a, double x)
    {
        var b = x + 1.0 - a;
        var c = 1.0 / TinyFloor;
        var d = 1.0 / b;
        var h = d;

        for (var i = 1; i <= MaxIterations; i++)
        {
            var an = -i * (i - a);
            b += 2.0;
            d = an * d + b;

            if (Math.Abs(d) < TinyFloor)
                d = TinyFloor;

            c = b + an / c;

            if (Math.Abs(c) < TinyFloor)
                c = TinyFloor;

            d = 1.0 / d;
            var del = d * c;
            h *= del;

            if (Math.Abs(del - 1.0) < Epsilon)
                break;
        }

        return Math.Exp(-x + a * Math.Log(x) - LogGamma(a)) * h;
    }
}
