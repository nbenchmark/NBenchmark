using Xunit;

namespace NBenchmark.Integration.xUnit.Tests;

/// <summary>
///     Serialises test classes that mutate the process-wide host assessment cached by
///     <c>BenchmarkAssert</c>. Without this, xUnit runs the classes in parallel and one
///     class's <c>ResetHostAssessment</c> can clobber another's <c>SetHostAssessment</c>.
/// </summary>
[CollectionDefinition(Name)]
public sealed class HostAssessmentCollection
{
    public const string Name = "HostAssessment";
}
