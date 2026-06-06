namespace NBenchmark.Stats;

/// <summary>
///     Quantile (inverse CDF) helpers for the standard normal and Student's t
///     distributions. Used to compute confidence intervals on the mean.
/// </summary>
/// <remarks>
///     These are self-contained numerical approximations — no external dependency.
///     The normal quantile uses Acklam's rational approximation (|error| &lt; 1.15e-9).
///     The Student's t quantile uses exact closed forms for 1 and 2 degrees of freedom
///     and the Cornish-Fisher expansion (Abramowitz &amp; Stegun 26.7.5) for higher df,
///     which is accurate to &lt; 1% for df ≥ 3 and converges to the normal as df → ∞.
/// </remarks>
public static class StudentT
{
    /// <summary>
    ///     Two-tailed critical t value for the given confidence level and degrees of freedom.
    ///     For example, <c>CriticalValue(0.95, 199) ≈ 1.97</c>.
    /// </summary>
    public static double CriticalValue(double confidenceLevel, int degreesOfFreedom)
    {
        if (degreesOfFreedom < 1 || confidenceLevel is <= 0 or >= 1)
            return double.NaN;

        // Two-tailed: split the remaining mass equally between both tails.
        var p = (1.0 + confidenceLevel) / 2.0;
        return InverseCdf(p, degreesOfFreedom);
    }

    /// <summary>
    ///     Inverse CDF (quantile) of Student's t-distribution with <paramref name="df" />
    ///     degrees of freedom at cumulative probability <paramref name="p" />.
    /// </summary>
    public static double InverseCdf(double p, int df)
    {
        if (df < 1 || p is <= 0 or >= 1)
            return double.NaN;

        // Exact closed forms for the low-df cases where the asymptotic
        // expansion below is least accurate.
        if (df == 1)
            return Math.Tan(Math.PI * (p - 0.5));

        if (df == 2)
        {
            var a = 2.0 * p - 1.0;
            return a * Math.Sqrt(2.0 / (1.0 - a * a));
        }

        // Cornish-Fisher expansion in powers of 1/df (A&S 26.7.5).
        var z = NormalQuantile(p);
        var z2 = z * z;
        var z3 = z2 * z;
        var z5 = z3 * z2;
        var z7 = z5 * z2;
        var z9 = z7 * z2;

        var g1 = (z3 + z) / 4.0;
        var g2 = (5.0 * z5 + 16.0 * z3 + 3.0 * z) / 96.0;
        var g3 = (3.0 * z7 + 19.0 * z5 + 17.0 * z3 - 15.0 * z) / 384.0;
        var g4 = (79.0 * z9 + 776.0 * z7 + 1482.0 * z5 - 1920.0 * z3 - 945.0 * z) / 92160.0;

        double n = df;
        return z + g1 / n + g2 / (n * n) + g3 / (n * n * n) + g4 / (n * n * n * n);
    }

    /// <summary>
    ///     Inverse CDF (quantile) of the standard normal distribution.
    ///     Peter Acklam's rational approximation; |error| &lt; 1.15e-9.
    /// </summary>
    public static double NormalQuantile(double p)
    {
        if (p <= 0)
            return double.NegativeInfinity;

        if (p >= 1)
            return double.PositiveInfinity;

        const double a1 = -3.969683028665376e+01;
        const double a2 = 2.209460984245205e+02;
        const double a3 = -2.759285104469687e+02;
        const double a4 = 1.383577518672690e+02;
        const double a5 = -3.066479806614716e+01;
        const double a6 = 2.506628277459239e+00;

        const double b1 = -5.447609879822406e+01;
        const double b2 = 1.615858368580409e+02;
        const double b3 = -1.556989798598866e+02;
        const double b4 = 6.680131188771972e+01;
        const double b5 = -1.328068155288572e+01;

        const double c1 = -7.784894002430293e-03;
        const double c2 = -3.223964580411365e-01;
        const double c3 = -2.400758277161838e+00;
        const double c4 = -2.549732539343734e+00;
        const double c5 = 4.374664141464968e+00;
        const double c6 = 2.938163982698783e+00;

        const double d1 = 7.784695709041462e-03;
        const double d2 = 3.224671290700398e-01;
        const double d3 = 2.445134137142996e+00;
        const double d4 = 3.754408661907416e+00;

        const double pLow = 0.02425;
        const double pHigh = 1.0 - pLow;

        double q, r;

        if (p < pLow)
        {
            q = Math.Sqrt(-2.0 * Math.Log(p));

            return (((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
                   ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0);
        }

        if (p <= pHigh)
        {
            q = p - 0.5;
            r = q * q;

            return (((((a1 * r + a2) * r + a3) * r + a4) * r + a5) * r + a6) * q /
                   (((((b1 * r + b2) * r + b3) * r + b4) * r + b5) * r + 1.0);
        }

        q = Math.Sqrt(-2.0 * Math.Log(1.0 - p));

        return -(((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
               ((((d1 * q + d2) * q + d3) * q + d4) * q + 1.0);
    }
}