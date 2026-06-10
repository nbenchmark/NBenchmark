using NBenchmark.Integration.Abstractions;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using NUnit.Framework.Internal.Builders;
using NUnit.Framework.Internal.Commands;

namespace NBenchmark.Integration.NUnit;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class PerformanceAttribute : NUnitAttribute, ISimpleTestBuilder, IWrapTestMethod, IApplyToTest, IPerformanceThresholds
{
    private readonly NUnitTestCaseBuilder _builder = new();

    public double MaxMeanNs { get; init; } = -1;
    public double MaxP95Ns { get; init; } = -1;
    public long MaxAllocatedBytes { get; init; } = -1;
    public string? BaselinePath { get; init; }
    public double MaxSlowdownRatio { get; init; } = 1.2;
    public int Iterations { get; init; }
    public int WarmupIterations { get; init; }
    public bool MeasureAllocations { get; init; }
    public OutlierMode OutlierMode { get; init; } = OutlierMode.RemoveTop5Percent;
    public double ConfidenceLevel { get; init; } = 0.95;

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

    public TestCommand Wrap(TestCommand command)
    {
        return new PerformanceCommand(command, this);
    }

    public void ApplyToTest(Test test)
    {
        if (test.RunState == RunState.NotRunnable)
            return;

        test.Properties.Set(PropertyNames.Description, $"Performance: {test.Name}");
    }
}