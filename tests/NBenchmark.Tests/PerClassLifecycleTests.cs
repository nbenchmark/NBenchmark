using NBenchmark.Attributes;
using NBenchmark.Discovery;
using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class PerClassLifecycleTests
{
    [Fact]
    public void TryCreateInstance_Success_Activator()
    {
        var instance = PerClassLifecycle.TryCreateInstance(typeof(SimpleType), null);
        Assert.NotNull(instance);
        Assert.IsType<SimpleType>(instance);
    }

    [Fact]
    public void TryCreateInstance_Success_Factory()
    {
        var instance = PerClassLifecycle.TryCreateInstance(typeof(SimpleType), _ => new SimpleType());
        Assert.NotNull(instance);
        Assert.IsType<SimpleType>(instance);
    }

    [Fact]
    public void TryCreateInstance_ActivatorFailure_ReturnsNull()
    {
        var instance = PerClassLifecycle.TryCreateInstance(typeof(NoDefaultCtor), null);
        Assert.Null(instance);
    }

    [Fact]
    public void TryCreateInstance_FactoryFailure_ReturnsNull()
    {
        var instance = PerClassLifecycle.TryCreateInstance(typeof(SimpleType), _ => throw new InvalidOperationException("factory failed"));
        Assert.Null(instance);
    }

    [Fact]
    public void TryRunSetup_Success_ReturnsTrue()
    {
        var flag = false;

        var suite = new BenchmarkSuiteDefinition(
            typeof(SimpleType),
            [],
            _ => { flag = true; });

        var (success, errors) = PerClassLifecycle.TryRunSetup(suite, new SimpleType(), MeasurementOptions.Default);

        Assert.True(success);
        Assert.Null(errors);
        Assert.True(flag);
    }

    [Fact]
    public void TryRunSetup_NoSetup_ReturnsTrue()
    {
        var suite = new BenchmarkSuiteDefinition(
            typeof(SimpleType),
            []);

        var (success, errors) = PerClassLifecycle.TryRunSetup(suite, new SimpleType(), MeasurementOptions.Default);

        Assert.True(success);
        Assert.Null(errors);
    }

    [Fact]
    public void TryRunSetup_SetupFailure_ReturnsErroredResults()
    {
        var suite = new BenchmarkSuiteDefinition(
            typeof(SimpleType),
            [
                new BenchmarkMethodDefinition(
                    typeof(SimpleType).GetMethod(nameof(SimpleType.Method))!,
                    new BenchmarkAttribute()),
            ],
            _ => throw new InvalidOperationException("setup failed"));

        var (success, errors) = PerClassLifecycle.TryRunSetup(suite, new SimpleType(), MeasurementOptions.Default);

        Assert.False(success);
        Assert.NotNull(errors);
        var result = Assert.Single(errors);
        Assert.True(result.Errored);
        Assert.Contains("setup failed", result.ErrorMessage);
    }

    [Fact]
    public async Task RunTeardown_CallsTeardownDelegate()
    {
        var teardownCalled = false;

        var suite = new BenchmarkSuiteDefinition(
            typeof(SimpleType),
            [],
            TeardownDelegate: _ => { teardownCalled = true; });

        await PerClassLifecycle.RunTeardown(suite, new SimpleType(), false, null);

        Assert.True(teardownCalled);
    }

    [Fact]
    public async Task RunTeardown_DisposesDisposable()
    {
        var disposable = new DisposableSpy();
        var suite = new BenchmarkSuiteDefinition(typeof(DisposableSpy), []);

        await PerClassLifecycle.RunTeardown(suite, disposable, false, null);

        Assert.True(disposable.Disposed);
    }

    [Fact]
    public async Task RunTeardown_DoesNotDispose_WhenFromFactory()
    {
        var disposable = new DisposableSpy();
        var suite = new BenchmarkSuiteDefinition(typeof(DisposableSpy), []);

        await PerClassLifecycle.RunTeardown(suite, disposable, true, null);

        Assert.False(disposable.Disposed);
    }

    [Fact]
    public async Task RunTeardown_RunsPostCleanup()
    {
        var cleanupCalled = false;
        var suite = new BenchmarkSuiteDefinition(typeof(SimpleType), []);

        await PerClassLifecycle.RunTeardown(suite, new SimpleType(), false, () => cleanupCalled = true);

        Assert.True(cleanupCalled);
    }

    [Fact]
    public async Task RunTeardown_TeardownFailure_DoesNotThrow()
    {
        var suite = new BenchmarkSuiteDefinition(
            typeof(SimpleType),
            [],
            TeardownDelegate: _ => throw new InvalidOperationException("teardown failed"));

        var ex = await Record.ExceptionAsync(() =>
            PerClassLifecycle.RunTeardown(suite, new SimpleType(), false, null));

        Assert.Null(ex);
    }

    [Fact]
    public async Task RunTeardown_AsyncDisposesAsyncDisposable()
    {
        var disposable = new AsyncDisposableSpy();
        var suite = new BenchmarkSuiteDefinition(typeof(AsyncDisposableSpy), []);

        await PerClassLifecycle.RunTeardown(suite, disposable, false, null);

        Assert.True(disposable.Disposed);
    }

    [Fact]
    public void TryCreateInstance_Propagates_OperationCanceledException()
    {
        Assert.Throws<OperationCanceledException>(() =>
            PerClassLifecycle.TryCreateInstance(typeof(SimpleType),
                _ => throw new OperationCanceledException()));
    }

    [Fact]
    public void TryRunSetup_Propagates_OperationCanceledException()
    {
        var suite = new BenchmarkSuiteDefinition(
            typeof(SimpleType),
            [],
            _ => throw new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() =>
            PerClassLifecycle.TryRunSetup(suite, new SimpleType(), MeasurementOptions.Default));
    }

    [Fact]
    public async Task RunTeardown_Propagates_OperationCanceledException()
    {
        var suite = new BenchmarkSuiteDefinition(
            typeof(SimpleType),
            [],
            TeardownDelegate: _ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PerClassLifecycle.RunTeardown(suite, new SimpleType(), false, null));
    }

    private sealed class SimpleType
    {
        public void Method()
        {
        }
    }

    private sealed class NoDefaultCtor
    {
        public NoDefaultCtor(int _)
        {
        }
    }

    private sealed class DisposableSpy : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class AsyncDisposableSpy : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
