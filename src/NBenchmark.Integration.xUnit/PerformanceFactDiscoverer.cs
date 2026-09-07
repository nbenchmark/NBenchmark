using Xunit.Abstractions;
using Xunit.Sdk;

namespace NBenchmark.Integration.xUnit;

/// <summary>
///     Turns a <see cref="PerformanceFactAttribute" />-annotated method into a
///     <see cref="PerformanceTestCase" />, carrying its thresholds along as <see cref="PerformanceTestData" />.
/// </summary>
public sealed class PerformanceFactDiscoverer : IXunitTestCaseDiscoverer
{
    private readonly IMessageSink _diagnosticMessageSink;

    /// <summary>Creates the discoverer with the diagnostic sink xUnit hands every discoverer.</summary>
    public PerformanceFactDiscoverer(IMessageSink diagnosticMessageSink)
    {
        _diagnosticMessageSink = diagnosticMessageSink;
    }

    /// <summary>Builds the single <see cref="PerformanceTestCase" /> for a <c>[PerformanceFact]</c> method.</summary>
    public IEnumerable<IXunitTestCase> Discover(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        ITestMethod testMethod,
        IAttributeInfo factAttribute)
    {
        var data = PerformanceTestData.FromThresholds(
            PerformanceAttributeParser.Parse(factAttribute));

        var testCase = new PerformanceTestCase(
            _diagnosticMessageSink,
            discoveryOptions.MethodDisplayOrDefault(),
            discoveryOptions.MethodDisplayOptionsOrDefault(),
            testMethod,
            data);

        yield return testCase;
    }
}
