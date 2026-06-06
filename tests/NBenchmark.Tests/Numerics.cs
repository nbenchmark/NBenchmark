using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Shared numeric assertion helpers for the statistics validation tests.
/// </summary>
internal static class Numerics
{
    /// <summary>
    ///     Asserts that <paramref name="actual" /> is within <paramref name="relativeTolerance" />
    ///     (relative error) of <paramref name="expected" />. Falls back to an absolute comparison
    ///     when <paramref name="expected" /> is zero.
    /// </summary>
    public static void AssertRelativeClose(double expected, double actual, double relativeTolerance)
    {
        if (expected == 0.0)
        {
            Assert.True(
                Math.Abs(actual) <= relativeTolerance,
                $"Expected ~0 (abs ≤ {relativeTolerance}), got {actual}.");
            return;
        }

        var relativeError = Math.Abs(actual - expected) / Math.Abs(expected);

        Assert.True(
            relativeError <= relativeTolerance,
            $"Expected {expected}, got {actual}; relative error {relativeError:E3} exceeds {relativeTolerance:E3}.");
    }
}
