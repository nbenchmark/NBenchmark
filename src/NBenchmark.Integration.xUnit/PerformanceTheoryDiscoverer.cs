using Xunit.Abstractions;
using Xunit.Sdk;

namespace NBenchmark.Integration.xUnit;

public sealed class PerformanceTheoryDiscoverer : TheoryDiscoverer
{
    public PerformanceTheoryDiscoverer(IMessageSink diagnosticMessageSink)
        : base(diagnosticMessageSink)
    {
    }

    protected override IEnumerable<IXunitTestCase> CreateTestCasesForDataRow(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        ITestMethod testMethod,
        IAttributeInfo theoryAttribute,
        object[] dataRow)
    {
        var data = PerformanceTestData.FromThresholds(
            PerformanceAttributeParser.Parse(theoryAttribute));

        var testCase = new PerformanceTestCase(
            DiagnosticMessageSink,
            discoveryOptions.MethodDisplayOrDefault(),
            discoveryOptions.MethodDisplayOptionsOrDefault(),
            testMethod,
            data,
            dataRow);

        yield return testCase;
    }

    protected override IEnumerable<IXunitTestCase> CreateTestCasesForSkip(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        ITestMethod testMethod,
        IAttributeInfo theoryAttribute,
        string skipReason)
    {
        var data = PerformanceTestData.FromThresholds(
            PerformanceAttributeParser.Parse(theoryAttribute),
            skipReason);

        var testCase = new PerformanceTestCase(
            DiagnosticMessageSink,
            discoveryOptions.MethodDisplayOrDefault(),
            discoveryOptions.MethodDisplayOptionsOrDefault(),
            testMethod,
            data);

        yield return testCase;
    }

    protected override IEnumerable<IXunitTestCase> CreateTestCasesForTheory(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        ITestMethod testMethod,
        IAttributeInfo theoryAttribute)
    {
        var data = PerformanceTestData.FromThresholds(
            PerformanceAttributeParser.Parse(theoryAttribute));

        var testCase = new PerformanceTestCase(
            DiagnosticMessageSink,
            discoveryOptions.MethodDisplayOrDefault(),
            discoveryOptions.MethodDisplayOptionsOrDefault(),
            testMethod,
            data);

        yield return testCase;
    }
}
