using System.Collections.Concurrent;
using System.Reflection;

namespace NBenchmark.Integration.MSTest.Tests;

[TestClass]
public sealed class PerformanceTestMethodIntegrationTests
{
    public static readonly ConcurrentDictionary<string, int> InvocationCounts = new();

    /// <summary>
    ///     Asserts the benchmark body actually executed.
    /// </summary>
    /// <remarks>
    ///     These assertions used to read an in-process invocation counter. The body now runs in a
    ///     worker process, so that counter stays at zero <i>because</i> isolation is working - it
    ///     would only be non-zero when the measurement silently fell back to the test host. The
    ///     measurement itself is the better evidence anyway: the engine cannot report timings for a
    ///     body it never invoked, and unlike a counter it cannot be incremented by anything else.
    /// </remarks>
    private static void AssertBodyRan(TestResult result, string what)
    {
        Assert.AreEqual(UnitTestOutcome.Passed, result.Outcome, $"{what} did not pass.");

        Assert.IsFalse(
            string.IsNullOrWhiteSpace(result.LogOutput),
            $"{what} produced no metrics, so its body did not run.");

        Assert.IsTrue(
            result.Duration > TimeSpan.Zero,
            $"{what} reported no elapsed time, so its body did not run.");
    }

    [TestMethod]
    public void PerformanceTestMethod_Runs_On_Static_Void_Method()
    {
        var key = nameof(StaticVoidBenchmark.StaticVoidRun);
        InvocationCounts[key] = 0;
        var result = ExecuteAttribute(typeof(StaticVoidBenchmark), key);

        Assert.AreEqual(UnitTestOutcome.Passed, result.Outcome);
        AssertBodyRan(result, "Static void benchmark body");
    }

    [TestMethod]
    public void PerformanceTestMethod_Runs_On_Static_Async_Method()
    {
        var key = nameof(StaticAsyncBenchmark.StaticAsyncRun);
        InvocationCounts[key] = 0;
        var result = ExecuteAttribute(typeof(StaticAsyncBenchmark), key);

        Assert.AreEqual(UnitTestOutcome.Passed, result.Outcome);
        AssertBodyRan(result, "Static async benchmark body");
    }

    [TestMethod]
    public void PerformanceTestMethod_Runs_On_Instance_Method()
    {
        var key = nameof(InstanceBenchmark.InstanceRun);
        InvocationCounts[key] = 0;
        var result = ExecuteAttribute(typeof(InstanceBenchmark), key);

        Assert.AreEqual(UnitTestOutcome.Passed, result.Outcome);
        AssertBodyRan(result, "Instance benchmark body");
    }

    [TestMethod]
    public void PerformanceTestMethod_Runs_On_Instance_Async_Method()
    {
        var key = nameof(InstanceAsyncBenchmark.InstanceAsyncRun);
        InvocationCounts[key] = 0;
        var result = ExecuteAttribute(typeof(InstanceAsyncBenchmark), key);

        Assert.AreEqual(UnitTestOutcome.Passed, result.Outcome);
        AssertBodyRan(result, "Instance async benchmark body");
    }

    [TestMethod]
    public void PerformanceTestMethod_Runs_On_Method_With_Arguments()
    {
        var key = nameof(ParameterizedBenchmark.RunWithArgument);

        // The body's success depends on the argument, so pass/fail proves the value arrived. An
        // in-process counter cannot show this any more - the body runs in a worker, where anything
        // it increments is invisible here - and comparing two runs' durations would make the
        // assertion a timing race rather than a fact.
        var accepted = ExecuteAttribute(typeof(ParameterizedBenchmark), key, [ParameterizedBenchmark.Accepted]);
        AssertBodyRan(accepted, "Parameterized benchmark");

        var rejected = ExecuteAttribute(typeof(ParameterizedBenchmark), key, [ParameterizedBenchmark.Rejected]);

        Assert.AreNotEqual(
            UnitTestOutcome.Passed,
            rejected.Outcome,
            "the rejected argument never reached the body: it should have thrown.");
    }

    private static TestResult ExecuteAttribute(Type testClass, string methodName, object[]? arguments = null)
    {
        var method = testClass.GetMethod(
                         methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                     ?? throw new InvalidOperationException($"Method {methodName} not found on {testClass.Name}.");

        var attribute = method.GetCustomAttribute<PerformanceTestMethodAttribute>()
                        ?? throw new InvalidOperationException($"Method {methodName} is missing [PerformanceTestMethod].");

        var testMethod = new StubTestMethod(testClass, method, attribute, arguments);
        var results = attribute.ExecuteAsync(testMethod).GetAwaiter().GetResult();

        Assert.AreEqual(1, results.Length);
        return results[0];
    }

    private sealed class StubTestMethod : ITestMethod
    {
        public StubTestMethod(
            Type testClass,
            MethodInfo methodInfo,
            PerformanceTestMethodAttribute attribute,
            object[]? arguments)
        {
            TestClassName = testClass.FullName ?? testClass.Name;
            TestMethodName = methodInfo.Name;
            MethodInfo = methodInfo;
            TestMethodAttribute = attribute;
            Arguments = arguments ?? [];
        }

        public string DisplayName => $"{TestClassName}.{TestMethodName}";
        public TestMethodAttribute TestMethodAttribute { get; }

        public string TestClassName { get; }
        public string TestMethodName { get; }
        public MethodInfo MethodInfo { get; }
        public object[] Arguments { get; }
        public Type ReturnType => MethodInfo.ReturnType;
        public ParameterInfo[] ParameterTypes => MethodInfo.GetParameters();

        public Attribute[] GetAllAttributes() =>
            MethodInfo.GetCustomAttributes(true).Cast<Attribute>().ToArray();

        public TAttributeType[] GetAttributes<TAttributeType>() where TAttributeType : Attribute
            => MethodInfo.GetCustomAttributes<TAttributeType>(true).ToArray();

        public Task<TestResult> InvokeAsync(object[]? args) => throw new NotSupportedException();

        public Attribute[] GetAllAttributes(bool inherit) =>
            MethodInfo.GetCustomAttributes(inherit).Cast<Attribute>().ToArray();

        public TAttributeType[] GetAttributes<TAttributeType>(bool inherit) where TAttributeType : Attribute
            => MethodInfo.GetCustomAttributes<TAttributeType>(inherit).ToArray();
    }
}

#pragma warning disable MSTEST0030 // Fixture types intentionally host PerformanceTestMethod methods without MSTest discovery semantics.

public static class StaticVoidBenchmark
{
    [PerformanceTestMethod(Samples = 3, WarmupSamples = 1)]
    public static void StaticVoidRun()
    {
        PerformanceTestMethodIntegrationTests.InvocationCounts.AddOrUpdate("StaticVoidRun", 1, (_, v) => v + 1);
        _ = 1 + 1;
    }
}

public static class StaticAsyncBenchmark
{
    [PerformanceTestMethod(Samples = 3, WarmupSamples = 1)]
    public static Task StaticAsyncRun()
    {
        PerformanceTestMethodIntegrationTests.InvocationCounts.AddOrUpdate("StaticAsyncRun", 1, (_, v) => v + 1);
        return Task.CompletedTask;
    }
}

public sealed class InstanceBenchmark
{
    [PerformanceTestMethod(Samples = 3, WarmupSamples = 1)]
    public void InstanceRun()
    {
        PerformanceTestMethodIntegrationTests.InvocationCounts.AddOrUpdate("InstanceRun", 1, (_, v) => v + 1);
        _ = 1 + 1;
    }
}

public sealed class InstanceAsyncBenchmark
{
    [PerformanceTestMethod(Samples = 3, WarmupSamples = 1)]
    public Task InstanceAsyncRun()
    {
        PerformanceTestMethodIntegrationTests.InvocationCounts.AddOrUpdate("InstanceAsyncRun", 1, (_, v) => v + 1);
        return Task.CompletedTask;
    }
}

public sealed class ParameterizedBenchmark
{
    /// <summary>The value this body accepts.</summary>
    public const int Accepted = 1;

    /// <summary>The value this body rejects, so a caller can prove the argument arrived.</summary>
    public const int Rejected = -1;

    /// <summary>
    ///     Succeeds or throws depending on its argument, so a caller can tell from the outcome alone
    ///     whether the value crossed the process boundary intact - with no reliance on timing.
    /// </summary>
    [PerformanceTestMethod(Samples = 3, WarmupSamples = 1)]
    public void RunWithArgument(int mode)
    {
        if (mode == Rejected)
            throw new InvalidOperationException($"argument {Rejected} reached the body");

        Thread.SpinWait(mode);
    }
}

#pragma warning restore MSTEST0030
