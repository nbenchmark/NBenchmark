using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests.Reporters;

public class ReporterRegistryAutoAttachTests : IDisposable
{
    public ReporterRegistryAutoAttachTests()
    {
        ReporterRegistry.Reset();
    }

    public void Dispose() => ReporterRegistry.Reset();

    [Fact]
    public void RegisterAutoAttach_Adds_To_AutoAttached_Not_Available()
    {
        ReporterRegistry.RegisterAutoAttach("auto", "Auto-attached fake", (_, _) => new StubReporter());

        Assert.Contains(ReporterRegistry.AutoAttached, r => r.Name == "auto");
        Assert.DoesNotContain(ReporterRegistry.Available, r => r.Name == "auto");
    }

    [Fact]
    public void AutoAttached_Starts_Empty_After_Reset() => Assert.Empty(ReporterRegistry.AutoAttached);

    [Fact]
    public void RegisterAutoAttach_Throws_On_Duplicate_Name_Case_Insensitive()
    {
        ReporterRegistry.RegisterAutoAttach("auto", "first", (_, _) => new StubReporter());

        Assert.Throws<InvalidOperationException>(() =>
            ReporterRegistry.RegisterAutoAttach("AUTO", "second", (_, _) => new StubReporter()));
    }

    [Fact]
    public void Register_Throws_When_Name_Already_AutoAttached()
    {
        ReporterRegistry.RegisterAutoAttach("auto", "auto", (_, _) => new StubReporter());

        Assert.Throws<InvalidOperationException>(() =>
            ReporterRegistry.Register("auto", "explicit", (_, _) => new StubReporter()));
    }

    [Fact]
    public void RegisterAutoAttach_Throws_When_Name_Already_Registered()
    {
        ReporterRegistry.Register("explicit", "explicit", (_, _) => new StubReporter());

        Assert.Throws<InvalidOperationException>(() =>
            ReporterRegistry.RegisterAutoAttach("explicit", "auto", (_, _) => new StubReporter()));
    }

    [Fact]
    public void RegisterAutoAttach_Null_Arguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ReporterRegistry.RegisterAutoAttach(null!, "d", (_, _) => new StubReporter()));

        Assert.Throws<ArgumentNullException>(() =>
            ReporterRegistry.RegisterAutoAttach("auto", null!, (_, _) => new StubReporter()));

        Assert.Throws<ArgumentNullException>(() =>
            ReporterRegistry.RegisterAutoAttach("auto", "d", null!));
    }

    [Fact]
    public void CreateAutoAttachedReporters_Returns_Fresh_Instances_Per_Call()
    {
        var factoryCallCount = 0;

        ReporterRegistry.RegisterAutoAttach(
            "auto",
            "auto",
            (_, _) =>
            {
                factoryCallCount++;
                return new StubReporter();
            });

        var first = ReporterRegistry.CreateAutoAttachedReporters(ReportDetail.Simple, new HashSet<string>());
        var second = ReporterRegistry.CreateAutoAttachedReporters(ReportDetail.Simple, new HashSet<string>());

        Assert.Single(first);
        Assert.Single(second);
        Assert.NotSame(first[0], second[0]);
        Assert.Equal(2, factoryCallCount);
    }

    [Fact]
    public void CreateAutoAttachedReporters_Passes_Detail_To_Factory()
    {
        var captured = ReportDetail.Simple;

        ReporterRegistry.RegisterAutoAttach(
            "auto",
            "auto",
            (_, detail) =>
            {
                captured = detail;
                return new StubReporter();
            });

        ReporterRegistry.CreateAutoAttachedReporters(ReportDetail.Advanced, new HashSet<string>());

        Assert.Equal(ReportDetail.Advanced, captured);
    }

    [Fact]
    public void CreateAutoAttachedReporters_Skips_Names_In_ExplicitNames_Set()
    {
        ReporterRegistry.RegisterAutoAttach("keep", "kept", (_, _) => new NamedStubReporter("keep"));
        ReporterRegistry.RegisterAutoAttach("skip", "skipped", (_, _) => new NamedStubReporter("skip"));

        var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skip" };
        var reporters = ReporterRegistry.CreateAutoAttachedReporters(ReportDetail.Simple, explicitNames);

        var name = Assert.Single(reporters).Name;
        Assert.Equal("keep", name);
    }

    [Fact]
    public void CreateAutoAttachedReporters_Dedup_Is_Case_Insensitive()
    {
        ReporterRegistry.RegisterAutoAttach("auto", "auto", (_, _) => new StubReporter());

        var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AUTO" };
        var reporters = ReporterRegistry.CreateAutoAttachedReporters(ReportDetail.Simple, explicitNames);

        Assert.Empty(reporters);
    }

    [Fact]
    public void CreateAutoAttachedReporters_Returns_Empty_When_None_Registered()
    {
        var reporters = ReporterRegistry.CreateAutoAttachedReporters(ReportDetail.Simple, new HashSet<string>());
        Assert.Empty(reporters);
    }

    [Fact]
    public void CreateAutoAttachedReporters_Preserves_Registration_Order()
    {
        ReporterRegistry.RegisterAutoAttach("first", "1", (_, _) => new NamedStubReporter("first"));
        ReporterRegistry.RegisterAutoAttach("second", "2", (_, _) => new NamedStubReporter("second"));
        ReporterRegistry.RegisterAutoAttach("third", "3", (_, _) => new NamedStubReporter("third"));

        var reporters = ReporterRegistry.CreateAutoAttachedReporters(ReportDetail.Simple, new HashSet<string>());

        Assert.Equal(["first", "second", "third"], reporters.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void CreateAutoAttachedReporters_Skips_Reporters_Whose_Factory_Throws()
    {
        ReporterRegistry.RegisterAutoAttach("boom", "throws", (_, _) => throw new InvalidOperationException("factory broke"));
        ReporterRegistry.RegisterAutoAttach("ok", "fine", (_, _) => new NamedStubReporter("ok"));

        var reporters = ReporterRegistry.CreateAutoAttachedReporters(ReportDetail.Simple, new HashSet<string>());

        var name = Assert.Single(reporters).Name;
        Assert.Equal("ok", name);
    }

    [Fact]
    public void Reset_Restores_AutoAttached_To_Empty()
    {
        ReporterRegistry.RegisterAutoAttach("temp", "temporary", (_, _) => new StubReporter());
        Assert.Contains(ReporterRegistry.AutoAttached, r => r.Name == "temp");

        ReporterRegistry.Reset();

        Assert.Empty(ReporterRegistry.AutoAttached);
    }

    [Fact]
    public void Reset_Preserves_AutoAttached_State_Captured_Before_First_Registration()
    {
        // Reset captures initial state lazily. The first Reset() call snapshots the current state.
        // Register an auto-attached reporter, then Reset twice: the first Reset snapshots the
        // registered state (test isolation contract), but on a fresh test class the constructor
        // already called Reset() once, so the snapshot is the empty initial state. Verify that
        // post-Reset state is empty and stays empty across further Reset calls.
        ReporterRegistry.Reset();
        Assert.Empty(ReporterRegistry.AutoAttached);

        ReporterRegistry.RegisterAutoAttach("temp", "temporary", (_, _) => new StubReporter());
        ReporterRegistry.Reset();
        Assert.Empty(ReporterRegistry.AutoAttached);

        ReporterRegistry.Reset();
        Assert.Empty(ReporterRegistry.AutoAttached);
    }

    [Fact]
    public void Reset_Restores_Both_Available_And_AutoAttached()
    {
        ReporterRegistry.Register("explicit", "explicit", (_, _) => new StubReporter());
        ReporterRegistry.RegisterAutoAttach("auto", "auto", (_, _) => new StubReporter());

        ReporterRegistry.Reset();

        Assert.DoesNotContain(ReporterRegistry.Available, r => r.Name == "explicit");
        Assert.Empty(ReporterRegistry.AutoAttached);

        // Seeds preserved
        Assert.Contains(ReporterRegistry.Available, r => r.Name == "json");
        Assert.Contains(ReporterRegistry.Available, r => r.Name == "markdown");
        Assert.Contains(ReporterRegistry.Available, r => r.Name == "csv");
    }

    private sealed class StubReporter : IReporter
    {
        public string Name => "stub";
        public ReportDetail Detail { get; set; } = ReportDetail.Simple;
        public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NamedStubReporter(string name) : IReporter
    {
        public string Name => name;
        public ReportDetail Detail { get; set; } = ReportDetail.Simple;
        public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
