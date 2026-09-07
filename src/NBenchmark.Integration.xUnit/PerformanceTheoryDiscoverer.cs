using Xunit.Abstractions;
using Xunit.Sdk;

namespace NBenchmark.Integration.xUnit;

/// <summary>
///     Turns each data row of a <see cref="PerformanceTheoryAttribute" />-annotated theory into a
///     <see cref="PerformanceTestCase" />, carrying its thresholds along as <see cref="PerformanceTestData" />.
/// </summary>
public sealed class PerformanceTheoryDiscoverer : TheoryDiscoverer
{
    /// <inheritdoc cref="TheoryDiscoverer(IMessageSink)" />
    public PerformanceTheoryDiscoverer(IMessageSink diagnosticMessageSink)
        : base(diagnosticMessageSink)
    {
    }

    /// <summary>Builds the <see cref="PerformanceTestCase" /> for one theory data row.</summary>
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

    /// <summary>Builds the <see cref="PerformanceTestCase" /> for a theory being skipped.</summary>
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

    /// <summary>Builds the <see cref="PerformanceTestCase" /> for a theory whose data rows are not pre-enumerated.</summary>
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
