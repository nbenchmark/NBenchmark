using NBenchmark.Observers;
using Xunit;

namespace NBenchmark.Tests.Reporters;

public class ObserverRegistryAutoAttachTests : IDisposable
{
    public ObserverRegistryAutoAttachTests()
    {
        ObserverRegistry.Reset();
    }

    public void Dispose() => ObserverRegistry.Reset();

    [Fact]
    public void RegisterAutoAttach_Adds_To_AutoAttached_Not_Available()
    {
        ObserverRegistry.RegisterAutoAttach("auto", "Auto-attached fake", () => new StubObserver("auto"));

        Assert.Contains(ObserverRegistry.AutoAttached, r => r.Name == "auto");
        Assert.DoesNotContain(ObserverRegistry.Available, r => r.Name == "auto");
    }

    [Fact]
    public void AutoAttached_Starts_Empty_After_Reset() => Assert.Empty(ObserverRegistry.AutoAttached);

    [Fact]
    public void RegisterAutoAttach_Throws_On_Duplicate_Name_Case_Insensitive()
    {
        ObserverRegistry.RegisterAutoAttach("auto", "first", () => new StubObserver("auto"));

        Assert.Throws<InvalidOperationException>(() =>
            ObserverRegistry.RegisterAutoAttach("AUTO", "second", () => new StubObserver("auto")));
    }

    [Fact]
    public void Register_Throws_When_Name_Already_AutoAttached()
    {
        ObserverRegistry.RegisterAutoAttach("auto", "auto", () => new StubObserver("auto"));

        Assert.Throws<InvalidOperationException>(() =>
            ObserverRegistry.Register("auto", "explicit", () => new StubObserver("auto")));
    }

    [Fact]
    public void RegisterAutoAttach_Throws_When_Name_Already_Registered()
    {
        ObserverRegistry.Register("explicit", "explicit", () => new StubObserver("explicit"));

        Assert.Throws<InvalidOperationException>(() =>
            ObserverRegistry.RegisterAutoAttach("explicit", "auto", () => new StubObserver("explicit")));
    }

    [Fact]
    public void RegisterAutoAttach_Null_Arguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ObserverRegistry.RegisterAutoAttach(null!, "d", () => new StubObserver("x")));

        Assert.Throws<ArgumentNullException>(() =>
            ObserverRegistry.RegisterAutoAttach("auto", null!, () => new StubObserver("x")));

        Assert.Throws<ArgumentNullException>(() =>
            ObserverRegistry.RegisterAutoAttach("auto", "d", null!));
    }

    [Fact]
    public void CreateAutoAttachedObservers_Returns_Fresh_Instances_Per_Call()
    {
        var factoryCallCount = 0;

        ObserverRegistry.RegisterAutoAttach(
            "auto",
            "auto",
            () =>
            {
                factoryCallCount++;
                return new StubObserver("auto");
            });

        var first = ObserverRegistry.CreateAutoAttachedObservers(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var second = ObserverRegistry.CreateAutoAttachedObservers(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Single(first);
        Assert.Single(second);
        Assert.NotSame(first[0], second[0]);
        Assert.Equal(2, factoryCallCount);
    }

    [Fact]
    public void CreateAutoAttachedObservers_Skips_Names_In_ExplicitNames_Set()
    {
        ObserverRegistry.RegisterAutoAttach("keep", "kept", () => new StubObserver("keep"));
        ObserverRegistry.RegisterAutoAttach("skip", "skipped", () => new StubObserver("skip"));

        var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skip" };
        var observers = ObserverRegistry.CreateAutoAttachedObservers(explicitNames);

        var observer = Assert.Single(observers);
        Assert.Equal("keep", observer.Name);
    }

    [Fact]
    public void CreateAutoAttachedObservers_Dedup_Is_Case_Insensitive()
    {
        ObserverRegistry.RegisterAutoAttach("auto", "auto", () => new StubObserver("auto"));

        var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AUTO" };
        var observers = ObserverRegistry.CreateAutoAttachedObservers(explicitNames);

        Assert.Empty(observers);
    }

    [Fact]
    public void CreateAutoAttachedObservers_Returns_Empty_When_None_Registered()
    {
        var observers = ObserverRegistry.CreateAutoAttachedObservers(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(observers);
    }

    [Fact]
    public void CreateAutoAttachedObservers_Preserves_Registration_Order()
    {
        ObserverRegistry.RegisterAutoAttach("first", "1", () => new StubObserver("first"));
        ObserverRegistry.RegisterAutoAttach("second", "2", () => new StubObserver("second"));
        ObserverRegistry.RegisterAutoAttach("third", "3", () => new StubObserver("third"));

        var observers = ObserverRegistry.CreateAutoAttachedObservers(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(new[] { "first", "second", "third" }, observers.Select(o => o.Name).ToArray());
    }

    [Fact]
    public void CreateAutoAttachedObservers_Skips_Observers_Whose_Factory_Throws()
    {
        ObserverRegistry.RegisterAutoAttach("boom", "throws", () => throw new InvalidOperationException("factory broke"));
        ObserverRegistry.RegisterAutoAttach("ok", "fine", () => new StubObserver("ok"));

        var observers = ObserverRegistry.CreateAutoAttachedObservers(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var observer = Assert.Single(observers);
        Assert.Equal("ok", observer.Name);
    }

    [Fact]
    public void TryCreate_Resolves_AutoAttached_Name()
    {
        // TryCreate checks both _entries and _autoAttachEntries so --observer <name>
        // resolves an auto-attached observer (e.g. --observer studio). The harness's
        // ResolveObserver dedup ensures the observer does not fire twice.
        ObserverRegistry.RegisterAutoAttach("auto", "auto", () => new StubObserver("auto"));

        var result = ObserverRegistry.TryCreate("auto", out var observer);

        Assert.True(result);
        Assert.NotNull(observer);
        Assert.Equal("auto", observer.Name);
    }

    [Fact]
    public void TryCreate_Resolves_AutoAttached_Name_Case_Insensitively()
    {
        ObserverRegistry.RegisterAutoAttach("auto", "auto", () => new StubObserver("auto"));

        var result = ObserverRegistry.TryCreate("AUTO", out var observer);

        Assert.True(result);
        Assert.NotNull(observer);
    }

    [Fact]
    public void TryCreate_Prefers_Explicit_When_Name_In_Both_Lists_Is_Forbidden()
    {
        // Register and RegisterAutoAttach reject the same name, so this cannot happen at
        // runtime. This test documents the invariant: only one list can hold a given name.
        ObserverRegistry.Register("explicit", "explicit", () => new StubObserver("explicit"));

        Assert.Throws<InvalidOperationException>(() =>
            ObserverRegistry.RegisterAutoAttach("explicit", "auto", () => new StubObserver("explicit")));

        // TryCreate still resolves the explicit entry.
        var result = ObserverRegistry.TryCreate("explicit", out var observer);
        Assert.True(result);
        Assert.Equal("explicit", observer!.Name);
    }

    [Fact]
    public void Reset_Restores_AutoAttached_To_Empty()
    {
        ObserverRegistry.RegisterAutoAttach("temp", "temporary", () => new StubObserver("temp"));
        Assert.Contains(ObserverRegistry.AutoAttached, r => r.Name == "temp");

        ObserverRegistry.Reset();

        Assert.Empty(ObserverRegistry.AutoAttached);
    }

    [Fact]
    public void Reset_Restores_Both_Available_And_AutoAttached()
    {
        ObserverRegistry.Register("explicit", "explicit", () => new StubObserver("explicit"));
        ObserverRegistry.RegisterAutoAttach("auto", "auto", () => new StubObserver("auto"));

        ObserverRegistry.Reset();

        Assert.DoesNotContain(ObserverRegistry.Available, r => r.Name == "explicit");
        Assert.Empty(ObserverRegistry.AutoAttached);
    }

    private sealed class StubObserver : IMeasurementObserver
    {
        public StubObserver(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public void OnPhase(in MeasurementPhaseEvent e)
        {
        }

        public void OnSample(in SampleEvent e)
        {
        }

        public void OnDetector(in DetectorStateEvent e)
        {
        }

        public void OnResult(BenchmarkResult result)
        {
        }
    }
}
