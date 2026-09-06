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
        ReporterRegistry.RegisterAutoAttach("auto", "Auto-attached fake", _ => new StubReporter());

        Assert.Contains(ReporterRegistry.AutoAttached, r => r.Name == "auto");
        Assert.DoesNotContain(ReporterRegistry.Available, r => r.Name == "auto");
    }

    [Fact]
    public void AutoAttached_Starts_Empty_After_Reset() => Assert.Empty(ReporterRegistry.AutoAttached);

    /// <summary>
    ///     <c>--reporter &lt;name&gt;</c> resolves an auto-attached reporter, the way
    ///     <c>--observer &lt;name&gt;</c> resolves an auto-attached observer. Without this the flag's
    ///     help line advertises a name the flag then refuses.
    /// </summary>
    [Fact]
    public void TryCreate_Resolves_An_AutoAttached_Name()
    {
        ReporterRegistry.RegisterAutoAttach("auto", "Auto-attached fake", _ => new StubReporter());

        Assert.True(ReporterRegistry.TryCreate("AUTO", null, out var reporter));
        Assert.NotNull(reporter);
    }

    [Fact]
    public void RegisterAutoAttach_Throws_On_Duplicate_Name_Case_Insensitive()
    {
        ReporterRegistry.RegisterAutoAttach("auto", "first", _ => new StubReporter());

        Assert.Throws<BenchmarkConfigurationException>(() =>
            ReporterRegistry.RegisterAutoAttach("AUTO", "second", _ => new StubReporter()));
    }

    [Fact]
    public void Register_Throws_When_Name_Already_AutoAttached()
    {
        ReporterRegistry.RegisterAutoAttach("auto", "auto", _ => new StubReporter());

        Assert.Throws<BenchmarkConfigurationException>(() =>
            ReporterRegistry.Register("auto", "explicit", _ => new StubReporter()));
    }

    [Fact]
    public void RegisterAutoAttach_Throws_When_Name_Already_Registered()
    {
        ReporterRegistry.Register("explicit", "explicit", _ => new StubReporter());

        Assert.Throws<BenchmarkConfigurationException>(() =>
            ReporterRegistry.RegisterAutoAttach("explicit", "auto", _ => new StubReporter()));
    }

    [Fact]
    public void RegisterAutoAttach_Null_Arguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ReporterRegistry.RegisterAutoAttach(null!, "d", _ => new StubReporter()));

        Assert.Throws<ArgumentNullException>(() =>
            ReporterRegistry.RegisterAutoAttach("auto", null!, _ => new StubReporter()));

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
            _ =>
            {
                factoryCallCount++;
                return new StubReporter();
            });

        var first = ReporterRegistry.CreateAutoAttachedReporters(new HashSet<string>());
        var second = ReporterRegistry.CreateAutoAttachedReporters(new HashSet<string>());

        Assert.Single(first);
        Assert.Single(second);
        Assert.NotSame(first[0], second[0]);
        Assert.Equal(2, factoryCallCount);
    }

    [Fact]
    public void CreateAutoAttachedReporters_Passes_A_Null_Output_Directory_To_Factory()
    {
        // Detail no longer reaches a reporter through its factory - it arrives with the results, in
        // the ReportContext - so the output directory is all the factory is handed, and an
        // auto-attached reporter is built without one.
        string? captured = "unset";

        ReporterRegistry.RegisterAutoAttach(
            "auto",
            "auto",
            dir =>
            {
                captured = dir;
                return new StubReporter();
            });

        ReporterRegistry.CreateAutoAttachedReporters(new HashSet<string>());

        Assert.Null(captured);
    }

    [Fact]
    public void CreateAutoAttachedReporters_Skips_Names_In_ExplicitNames_Set()
    {
        ReporterRegistry.RegisterAutoAttach("keep", "kept", _ => new NamedStubReporter("keep"));
        ReporterRegistry.RegisterAutoAttach("skip", "skipped", _ => new NamedStubReporter("skip"));

        var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skip" };
        var reporters = ReporterRegistry.CreateAutoAttachedReporters(explicitNames);

        var name = Assert.Single(reporters).Name;
        Assert.Equal("keep", name);
    }

    [Fact]
    public void CreateAutoAttachedReporters_Dedup_Is_Case_Insensitive()
    {
        ReporterRegistry.RegisterAutoAttach("auto", "auto", _ => new StubReporter());

        var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AUTO" };
        var reporters = ReporterRegistry.CreateAutoAttachedReporters(explicitNames);

        Assert.Empty(reporters);
    }

    [Fact]
    public void CreateAutoAttachedReporters_Returns_Empty_When_None_Registered()
    {
        var reporters = ReporterRegistry.CreateAutoAttachedReporters(new HashSet<string>());
        Assert.Empty(reporters);
    }

    [Fact]
    public void CreateAutoAttachedReporters_Preserves_Registration_Order()
    {
        ReporterRegistry.RegisterAutoAttach("first", "1", _ => new NamedStubReporter("first"));
        ReporterRegistry.RegisterAutoAttach("second", "2", _ => new NamedStubReporter("second"));
        ReporterRegistry.RegisterAutoAttach("third", "3", _ => new NamedStubReporter("third"));

        var reporters = ReporterRegistry.CreateAutoAttachedReporters(new HashSet<string>());

        Assert.Equal(["first", "second", "third"], reporters.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void CreateAutoAttachedReporters_Skips_Reporters_Whose_Factory_Throws()
    {
        ReporterRegistry.RegisterAutoAttach("boom", "throws", _ => throw new InvalidOperationException("factory broke"));
        ReporterRegistry.RegisterAutoAttach("ok", "fine", _ => new NamedStubReporter("ok"));

        var reporters = ReporterRegistry.CreateAutoAttachedReporters(new HashSet<string>());

        var name = Assert.Single(reporters).Name;
        Assert.Equal("ok", name);
    }

    [Fact]
    public void Reset_Restores_AutoAttached_To_Empty()
    {
        ReporterRegistry.RegisterAutoAttach("temp", "temporary", _ => new StubReporter());
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

        ReporterRegistry.RegisterAutoAttach("temp", "temporary", _ => new StubReporter());
        ReporterRegistry.Reset();
        Assert.Empty(ReporterRegistry.AutoAttached);

        ReporterRegistry.Reset();
        Assert.Empty(ReporterRegistry.AutoAttached);
    }

    [Fact]
    public void Reset_Restores_Both_Available_And_AutoAttached()
    {
        ReporterRegistry.Register("explicit", "explicit", _ => new StubReporter());
        ReporterRegistry.RegisterAutoAttach("auto", "auto", _ => new StubReporter());

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
        public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, ReportContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NamedStubReporter(string name) : IReporter
    {
        public string Name => name;
        public Task ReportAsync(IReadOnlyList<BenchmarkResult> results, ReportContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
