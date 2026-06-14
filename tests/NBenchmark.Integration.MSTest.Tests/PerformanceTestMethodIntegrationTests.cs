using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NBenchmark.Integration.MSTest.Tests;

[TestClass]
public sealed class PerformanceTestMethodIntegrationTests
{
    public static readonly ConcurrentDictionary<string, int> InvocationCounts = new();

    [TestMethod]
    public void PerformanceTestMethod_Runs_On_Static_Void_Method()
    {
        var key = nameof(StaticVoidBenchmark.StaticVoidRun);
        InvocationCounts[key] = 0;
        var result = ExecuteAttribute(typeof(StaticVoidBenchmark), key);

        Assert.AreEqual(UnitTestOutcome.Passed, result.Outcome);
        Assert.IsTrue(InvocationCounts[key] > 0, "Static void benchmark body did not run.");
    }

    [TestMethod]
    public void PerformanceTestMethod_Runs_On_Static_Async_Method()
    {
        var key = nameof(StaticAsyncBenchmark.StaticAsyncRun);
        InvocationCounts[key] = 0;
        var result = ExecuteAttribute(typeof(StaticAsyncBenchmark), key);

        Assert.AreEqual(UnitTestOutcome.Passed, result.Outcome);
        Assert.IsTrue(InvocationCounts[key] > 0, "Static async benchmark body did not run.");
    }

    [TestMethod]
    public void PerformanceTestMethod_Runs_On_Instance_Method()
    {
        var key = nameof(InstanceBenchmark.InstanceRun);
        InvocationCounts[key] = 0;
        var result = ExecuteAttribute(typeof(InstanceBenchmark), key);

        Assert.AreEqual(UnitTestOutcome.Passed, result.Outcome);
        Assert.IsTrue(InvocationCounts[key] > 0, "Instance benchmark body did not run.");
    }

    [TestMethod]
    public void PerformanceTestMethod_Runs_On_Instance_Async_Method()
    {
        var key = nameof(InstanceAsyncBenchmark.InstanceAsyncRun);
        InvocationCounts[key] = 0;
        var result = ExecuteAttribute(typeof(InstanceAsyncBenchmark), key);

        Assert.AreEqual(UnitTestOutcome.Passed, result.Outcome);
        Assert.IsTrue(InvocationCounts[key] > 0, "Instance async benchmark body did not run.");
    }

    [TestMethod]
    public void PerformanceTestMethod_Runs_On_Method_With_Arguments()
    {
        var key = nameof(ParameterizedBenchmark.RunWithArgument);
        InvocationCounts[key] = 0;
        var result = ExecuteAttribute(typeof(ParameterizedBenchmark), key, [2]);

        Assert.AreEqual(UnitTestOutcome.Passed, result.Outcome);
        Assert.IsTrue(InvocationCounts[key] > 0, "Parameterized benchmark body did not run with supplied arguments.");
    }

    private static TestResult ExecuteAttribute(Type testClass, string methodName, object[]? arguments = null)
    {
        var method = testClass.GetMethod(
                         methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                     ?? throw new InvalidOperationException($"Method {methodName} not found on {testClass.Name}.");

        var attribute = method.GetCustomAttribute<PerformanceTestMethodAttribute>()
                        ?? throw new InvalidOperationException($"Method {methodName} is missing [PerformanceTestMethod].");

        var testMethod = new StubTestMethod(testClass, method, attribute, arguments);
        var results = attribute.Execute(testMethod);

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

        public Attribute[] GetAllAttributes(bool inherit) =>
            MethodInfo.GetCustomAttributes(inherit).Cast<Attribute>().ToArray();

        public TAttributeType[] GetAttributes<TAttributeType>(bool inherit) where TAttributeType : Attribute
            => MethodInfo.GetCustomAttributes<TAttributeType>(inherit).ToArray();

        public TestResult Invoke(object[]? args) => throw new NotSupportedException();

        public IEnumerable<Attribute> GetAllAttributes() =>
            MethodInfo.GetCustomAttributes();
    }
}

public static class StaticVoidBenchmark
{
    [PerformanceTestMethod(Iterations = 3, WarmupIterations = 1)]
    public static void StaticVoidRun()
    {
        PerformanceTestMethodIntegrationTests.InvocationCounts.AddOrUpdate("StaticVoidRun", 1, (_, v) => v + 1);
        _ = 1 + 1;
    }
}

public static class StaticAsyncBenchmark
{
    [PerformanceTestMethod(Iterations = 3, WarmupIterations = 1)]
    public static Task StaticAsyncRun()
    {
        PerformanceTestMethodIntegrationTests.InvocationCounts.AddOrUpdate("StaticAsyncRun", 1, (_, v) => v + 1);
        return Task.CompletedTask;
    }
}

public sealed class InstanceBenchmark
{
    [PerformanceTestMethod(Iterations = 3, WarmupIterations = 1)]
    public void InstanceRun()
    {
        PerformanceTestMethodIntegrationTests.InvocationCounts.AddOrUpdate("InstanceRun", 1, (_, v) => v + 1);
        _ = 1 + 1;
    }
}

public sealed class InstanceAsyncBenchmark
{
    [PerformanceTestMethod(Iterations = 3, WarmupIterations = 1)]
    public Task InstanceAsyncRun()
    {
        PerformanceTestMethodIntegrationTests.InvocationCounts.AddOrUpdate("InstanceAsyncRun", 1, (_, v) => v + 1);
        return Task.CompletedTask;
    }
}

public sealed class ParameterizedBenchmark
{
    [PerformanceTestMethod(Iterations = 3, WarmupIterations = 1)]
    public void RunWithArgument(int increment)
    {
        PerformanceTestMethodIntegrationTests.InvocationCounts.AddOrUpdate(
            nameof(RunWithArgument), increment, (_, value) => value + increment);
    }
}
