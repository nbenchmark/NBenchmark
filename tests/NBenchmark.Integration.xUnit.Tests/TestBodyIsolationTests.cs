using System.Reflection;
using NBenchmark.Integration.Abstractions;
using Xunit;

namespace NBenchmark.Integration.xUnit.Tests;

/// <summary>
///     Which test bodies can be measured in a worker, and how clearly the rest explain themselves.
///     <para>
///         A test integration exists to gate, and a gate reading a host-process number is not being
///         conservative - in-process measurement fabricated a 2.80x ratio between bodies of provably
///         identical cost. So the reason a body could not be isolated has to reach the result rather
///         than being absorbed silently.
///     </para>
/// </summary>
public sealed class TestBodyIsolationTests
{
    private static MethodInfo Method<T>(string name)
        => typeof(T).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.Static)!;

    /// <summary>The ordinary case: a plain test class a worker can build for itself.</summary>
    [Fact]
    public void PlainInstanceMethod_CanIsolate()
    {
        var decision = TestBodyIsolation.Classify(
            Method<PlainTests>(nameof(PlainTests.Measure)), new PlainTests(), []);

        Assert.True(decision.CanIsolate, decision.Reason);
        Assert.Equal("Isolated", decision.Status);
    }

    /// <summary>A static method needs no instance at all.</summary>
    [Fact]
    public void StaticMethod_CanIsolate()
    {
        var decision = TestBodyIsolation.Classify(
            Method<PlainTests>(nameof(PlainTests.MeasureStatic)), instance: null, []);

        Assert.True(decision.CanIsolate, decision.Reason);
    }

    /// <summary>
    ///     A class the framework injects into - a fixture, an output helper - cannot be rebuilt in a
    ///     worker. The refusal names the injected types, because that is what tells the author which
    ///     dependency is the obstacle.
    /// </summary>
    [Fact]
    public void ClassWithInjectedConstructor_IsRefusedAsLiveFixture()
    {
        var decision = TestBodyIsolation.Classify(
            Method<FixtureTests>(nameof(FixtureTests.Measure)), new FixtureTests(new Fixture()), []);

        Assert.False(decision.CanIsolate);
        Assert.Equal("InProcessLiveFixture", decision.Status);
        Assert.Contains("Fixture", decision.Reason);
        Assert.Contains("parameterless constructor", decision.Reason);
    }

    /// <summary>Simple values travel, so a parameterized test is still isolatable.</summary>
    [Theory]
    [InlineData(42)]
    [InlineData("text")]
    [InlineData(true)]
    public void SimpleArguments_CanIsolate(object argument)
    {
        var decision = TestBodyIsolation.Classify(
            Method<PlainTests>(nameof(PlainTests.Measure)), new PlainTests(), [argument]);

        Assert.True(decision.CanIsolate, decision.Reason);
    }

    /// <summary>
    ///     An object-graph argument is a live thing, and is refused rather than reconstructed.
    ///     Reconstructing it is the tempting move and the wrong one: it would measure a
    ///     differently-populated object while reporting the same test name.
    /// </summary>
    [Fact]
    public void ObjectGraphArgument_IsRefusedAsLiveFixture()
    {
        var decision = TestBodyIsolation.Classify(
            Method<PlainTests>(nameof(PlainTests.Measure)), new PlainTests(), [new Fixture()]);

        Assert.False(decision.CanIsolate);
        Assert.Equal("InProcessLiveFixture", decision.Status);
        Assert.Contains("Fixture", decision.Reason);
    }

    /// <summary>
    ///     Null is a value, not a live object - refusing it would push perfectly ordinary
    ///     <c>[InlineData(null)]</c> cases onto the in-process path for no reason.
    /// </summary>
    [Fact]
    public void NullArgument_CanIsolate()
    {
        var decision = TestBodyIsolation.Classify(
            Method<PlainTests>(nameof(PlainTests.Measure)), new PlainTests(), [null]);

        Assert.True(decision.CanIsolate, decision.Reason);
    }

    /// <summary>Generic definitions are not addressed across the boundary yet, and say so.</summary>
    [Fact]
    public void GenericMethod_IsRefusedAsUnaddressable()
    {
        var decision = TestBodyIsolation.Classify(
            Method<PlainTests>(nameof(PlainTests.MeasureGeneric)), new PlainTests(), []);

        Assert.False(decision.CanIsolate);
        Assert.Equal("InProcessUnaddressablePlan", decision.Status);
        Assert.Contains("generic", decision.Reason);
    }

    private sealed class PlainTests
    {
        public void Measure()
        {
        }

        public static void MeasureStatic()
        {
        }

        public void MeasureGeneric<T>()
        {
        }
    }

    private sealed class FixtureTests(Fixture fixture)
    {
        public void Measure() => _ = fixture;
    }

    private sealed class Fixture;
}
