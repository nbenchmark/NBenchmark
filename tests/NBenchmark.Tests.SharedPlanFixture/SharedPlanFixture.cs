using NBenchmark;

namespace NBenchmark.Tests.SharedPlanFixture;

/// <summary>
///     A [BenchmarkPlan] factory declared in a library the fixture executable only <i>references</i> -
///     the shape A11 exists for. A multi-runtime suite's whole reason to share a plan factory across
///     per-runtime projects is a library exactly like this one; by-name addressing is how it is found
///     under a different target framework's build, where no metadata token from this build means
///     anything.
/// </summary>
public static class SharedHelperPlan
{
    public const string SuiteName = "shared-plan";

    public const string BenchmarkName = "only";

    [BenchmarkPlan]
    public static BenchmarkSuite BuildSuite() =>
        new BenchmarkSuite(SuiteName)
            .Add(BenchmarkName, () => Thread.SpinWait(200))
            .WithSamples(8)
            .WithWarmupSamples(1)
            .WithOpsPerSample(1);
}
