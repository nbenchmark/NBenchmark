using Xunit.Abstractions;
using Xunit.Sdk;

namespace NBenchmark.Extensions.xUnit;

public sealed class PerformanceFactDiscoverer : IXunitTestCaseDiscoverer
{
    private readonly IMessageSink _diagnosticMessageSink;

    public PerformanceFactDiscoverer(IMessageSink diagnosticMessageSink)
    {
        _diagnosticMessageSink = diagnosticMessageSink;
    }

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
