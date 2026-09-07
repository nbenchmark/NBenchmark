using NBenchmark.Integration.Abstractions;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using NUnit.Framework.Internal.Builders;
using NUnit.Framework.Internal.Commands;

namespace NBenchmark.Integration.NUnit;

/// <summary>
///     Marks an NUnit test method as a performance benchmark, gated on the thresholds set here. Builds the
///     test via <see cref="ISimpleTestBuilder" /> and wraps its execution in a <see cref="PerformanceCommand" />.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PerformanceAttribute : NUnitAttribute, ISimpleTestBuilder, IWrapTestMethod, IApplyToTest, IPerformanceThresholds
{
    private readonly NUnitTestCaseBuilder _builder = new();

    /// <summary>Sets the test's description to identify it as a performance benchmark.</summary>
    public void ApplyToTest(Test test)
    {
        if (test.RunState == RunState.NotRunnable)
            return;

        test.Properties.Set(PropertyNames.Description, $"Performance: {test.Name}");
    }

    /// <summary>Maximum mean time per operation in nanoseconds, or <see cref="IPerformanceThresholds.Unset" />.</summary>
    public double MaxMeanNs { get; init; } = IPerformanceThresholds.Unset;

    /// <summary>
    ///     Maximum median time per operation in nanoseconds. The median is the statistic the reports
    ///     lead with; prefer it over <see cref="MaxMeanNs" /> unless the average is what is meant.
    ///     See <see cref="IPerformanceThresholds.MaxMedianNs" />.
    /// </summary>
    public double MaxMedianNs { get; init; } = IPerformanceThresholds.Unset;

    /// <summary>Maximum 95th-percentile time per operation in nanoseconds, or <see cref="IPerformanceThresholds.Unset" />.</summary>
    public double MaxP95Ns { get; init; } = IPerformanceThresholds.Unset;

    /// <summary>Maximum mean bytes allocated per operation, or <see cref="IPerformanceThresholds.UnsetBytes" />.</summary>
    public long MaxAllocatedBytes { get; init; } = IPerformanceThresholds.UnsetBytes;

    /// <summary>
    ///     The name of the method to measure alongside this one as the denominator of
    ///     <see cref="MaxSlowdownRatio" />, or <c>null</c> when there is no comparison.
    /// </summary>
    public string? ReferenceMethod { get; init; }

    /// <summary>
    ///     Maximum ratio of this measurement to <see cref="ReferenceMethod" />'s, or
    ///     <see cref="IPerformanceThresholds.Unset" />. Reads as "no more than N times slower than the reference".
    /// </summary>
    public double MaxSlowdownRatio { get; init; } = IPerformanceThresholds.Unset;

    /// <summary>Measured samples to take, or <see cref="IPerformanceThresholds.AutoSampleCount" />.</summary>
    public int Samples { get; init; } = IPerformanceThresholds.AutoSampleCount;

    /// <summary>Warmup samples to take before measuring, or <see cref="IPerformanceThresholds.AutoSampleCount" />.</summary>
    public int WarmupSamples { get; init; } = IPerformanceThresholds.AutoSampleCount;

    /// <summary>Whether to measure allocations as well as time.</summary>
    public bool MeasureAllocations { get; init; }

    /// <summary>Which samples to trim before the statistics are computed.</summary>
    public OutlierMode OutlierMode { get; init; } = OutlierMode.IqrFence;

    /// <summary>The confidence level for the reported interval, e.g. <c>0.95</c>.</summary>
    public double ConfidenceLevel { get; init; } = 0.95;

    /// <summary>
    ///     How far past an absolute threshold a measurement may land before the gate fails, as a
    ///     multiplier of the threshold. <c>1.0</c> fails at the threshold exactly.
    /// </summary>
    public double MaxAbsoluteThresholdTolerance { get; init; } = 1.0;

    /// <summary>
    ///     Worker processes to measure this test in. Defaults to 1; two or more give the ratio gate a
    ///     paired confidence interval. See <see cref="IPerformanceThresholds.LaunchCount" />.
    /// </summary>
    public int LaunchCount { get; init; } = 1;

    // No RequireIsolation property - see PerformanceTestMethodAttribute. Defaults to true via
    // IPerformanceThresholds; opt out with [AllowInProcessGate].

    /// <summary>Builds the underlying NUnit <see cref="TestMethod" />, working around its non-void/Task restrictions.</summary>
    public TestMethod BuildFrom(IMethodInfo method, Test? suite)
    {
        var parms = new TestCaseParameters();

        // NUnit's NUnitTestCaseBuilder rejects non-void methods unless TestCaseParameters.HasExpectedResult is set,
        // and rejects Task/ValueTask (whose inner result is void) when HasExpectedResult is set. PerformanceCommand
        // runs the method via BenchmarkRunner, never invoking the inner TestMethodCommand, so the ExpectedResult
        // placeholder is never compared.
        if (RequiresExpectedResultPlaceholder(method.ReturnType.Type))
            parms.ExpectedResult = null;

        return _builder.BuildTestMethod(method, suite, parms);
    }

    /// <summary>Wraps the test's command so it measures with NBenchmark instead of invoking the method directly.</summary>
    public TestCommand Wrap(TestCommand command) => new PerformanceCommand(command, this);

    private static bool RequiresExpectedResultPlaceholder(Type returnType)
    {
        if (returnType == typeof(void))
            return false;

        if (returnType == typeof(Task) || returnType == typeof(ValueTask))
            return false;

        if (returnType.IsGenericType)
        {
            var def = returnType.GetGenericTypeDefinition();

            if (def == typeof(Task<>) || def == typeof(ValueTask<>))
                return true;
        }

        return true;
    }
}
