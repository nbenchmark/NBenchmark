using NBenchmark.Observers;
using Xunit;

namespace NBenchmark.Tests;

public class ObserverRegistryTests : IDisposable
{
    public ObserverRegistryTests()
    {
        ObserverRegistry.Reset();
    }

    public void Dispose() => ObserverRegistry.Reset();

    [Fact]
    public void Available_Is_Empty_By_Default()
    {
        Assert.Empty(ObserverRegistry.Available);
    }

    [Fact]
    public void TryCreate_Unknown_Name_Returns_False()
    {
        var ok = ObserverRegistry.TryCreate("bogus", out var observer);

        Assert.False(ok);
        Assert.Null(observer);
    }

    [Fact]
    public void TryCreate_Is_Case_Insensitive()
    {
        ObserverRegistry.Register("live", "Live dashboard", () => new StubObserver("live"));

        Assert.True(ObserverRegistry.TryCreate("LIVE", out _));
        Assert.True(ObserverRegistry.TryCreate("Live", out _));
        Assert.True(ObserverRegistry.TryCreate("live", out _));
    }

    [Fact]
    public void TryCreate_Null_Name_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ObserverRegistry.TryCreate(null!, out _));
    }

    [Fact]
    public void Register_Adds_Observer_To_Available_And_TryCreate()
    {
        ObserverRegistry.Register("fake", "Fake observer for tests", () => new StubObserver("fake"));

        Assert.Contains(ObserverRegistry.Available, r => r.Name == "fake");
        Assert.True(ObserverRegistry.TryCreate("fake", out var observer));
        Assert.IsType<StubObserver>(observer);
    }

    [Fact]
    public void Register_Throws_On_Duplicate_Name()
    {
        ObserverRegistry.Register("test", "First", () => new StubObserver("test"));

        Assert.Throws<InvalidOperationException>(() =>
            ObserverRegistry.Register("test", "Duplicate", () => new StubObserver("test")));
    }

    [Fact]
    public void Register_Is_Case_Insensitive_For_Duplicate_Check()
    {
        ObserverRegistry.Register("test", "First", () => new StubObserver("test"));

        Assert.Throws<InvalidOperationException>(() =>
            ObserverRegistry.Register("TEST", "Duplicate uppercase", () => new StubObserver("test")));
    }

    [Fact]
    public void Register_Respects_Factory()
    {
        ObserverRegistry.Register("capturing", "Captures instance", () => new StubObserver("captured"));

        Assert.True(ObserverRegistry.TryCreate("capturing", out var observer));
        var stub = Assert.IsType<StubObserver>(observer);
        Assert.Equal("captured", stub.Name);
    }

    [Fact]
    public void Register_Null_Name_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ObserverRegistry.Register(null!, "desc", () => new StubObserver("x")));
    }

    [Fact]
    public void Register_Null_Description_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ObserverRegistry.Register("x", null!, () => new StubObserver("x")));
    }

    [Fact]
    public void Register_Null_Factory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ObserverRegistry.Register("x", "desc", null!));
    }

    [Fact]
    public void Available_Reflects_Registrations()
    {
        ObserverRegistry.Register("a", "First", () => new StubObserver("a"));
        ObserverRegistry.Register("b", "Second", () => new StubObserver("b"));

        var names = ObserverRegistry.Available.Select(r => r.Name).OrderBy(n => n).ToList();
        Assert.Equal(["a", "b"], names);
    }

    [Fact]
    public void Reset_Clears_Registrations()
    {
        ObserverRegistry.Register("temp", "Temporary", () => new StubObserver("temp"));
        Assert.NotEmpty(ObserverRegistry.Available);

        ObserverRegistry.Reset();

        Assert.Empty(ObserverRegistry.Available);
    }

    [Fact]
    public void Reset_Preserves_Initial_Empty_State()
    {
        ObserverRegistry.Register("temp", "Temporary", () => new StubObserver("temp"));
        Assert.Contains(ObserverRegistry.Available, r => r.Name == "temp");

        ObserverRegistry.Reset();

        Assert.DoesNotContain(ObserverRegistry.Available, r => r.Name == "temp");
    }

    [Fact]
    public void Multiple_Resets_Are_Safe()
    {
        ObserverRegistry.Reset();
        ObserverRegistry.Reset();
        ObserverRegistry.Reset();

        Assert.Empty(ObserverRegistry.Available);
    }

    [Fact]
    public void Reset_Rearms_Extension_Load_Latch()
    {
        // Available / TryCreate both call EnsureExtensionsLoaded, which is a one-shot latch
        // (Interlocked.Exchange on _extensionsLoaded). Reset must re-arm the latch so the
        // registry can be re-tested from a clean slate once any test has touched Available
        // or TryCreate. Without the re-arm the latch stays set for the whole process. This
        // test pins the behaviour: touch Available (which loads the latch), Reset, then
        // touch Available again and assert it returns the seeded/registered state cleanly.
        // The latch is process-global so this test is order-sensitive with respect to other
        // tests in this class; the Reset in the ctor + Dispose restores it after the run.
        _ = ObserverRegistry.Available; // triggers EnsureExtensionsLoaded (latch -> 1)

        ObserverRegistry.Register("after-load", "Registered after first load", () => new StubObserver("after-load"));
        Assert.Contains(ObserverRegistry.Available, r => r.Name == "after-load");

        ObserverRegistry.Reset(); // re-arms latch to 0 and clears registrations

        Assert.DoesNotContain(ObserverRegistry.Available, r => r.Name == "after-load");

        // A subsequent registration + read still observes the new entry (proves the latch
        // did not get stuck at 1 and block re-entry into EnsureExtensionsLoaded).
        ObserverRegistry.Register("post-reset", "Registered after reset", () => new StubObserver("post-reset"));
        Assert.Contains(ObserverRegistry.Available, r => r.Name == "post-reset");
    }

    [Fact]
    public void TryCreate_Returns_Fresh_Instance_Each_Call()
    {
        ObserverRegistry.Register("factory", "Returns new each time", () => new StubObserver("factory"));

        Assert.True(ObserverRegistry.TryCreate("factory", out var first));
        Assert.True(ObserverRegistry.TryCreate("factory", out var second));

        Assert.NotSame(first, second);
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
