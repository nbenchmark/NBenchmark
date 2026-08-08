using NBenchmark.Attributes;
using NBenchmark.Discovery;
using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkLifecycleTests
{
    [Fact]
    public void CreateInstance_Success_Activator()
    {
        var created = BenchmarkLifecycle.CreateInstance(typeof(SimpleType), null, out _);
        Assert.NotNull(created);
        Assert.IsType<SimpleType>(created!.Value.Instance);
    }

    [Fact]
    public void CreateInstance_Success_Factory()
    {
        var created = BenchmarkLifecycle.CreateInstance(typeof(SimpleType), _ => InstanceHandle.NoTeardown(new SimpleType()), out _);
        Assert.NotNull(created);
        Assert.IsType<SimpleType>(created!.Value.Instance);
    }

    [Fact]
    public void CreateInstance_ActivatorFailure_ReturnsNull()
    {
        var created = BenchmarkLifecycle.CreateInstance(typeof(NoDefaultCtor), null, out var failure);

        Assert.Null(created);

        // The reason is returned, not only printed. It is what the errored row carries, and a row
        // saying only "could not be instantiated" sends the reader nowhere.
        Assert.NotNull(failure);
        Assert.Contains(nameof(NoDefaultCtor), failure);
        Assert.Contains("parameterless constructor", failure);
    }

    [Fact]
    public void CreateInstance_FactoryFailure_ReturnsNull()
    {
        var created = BenchmarkLifecycle.CreateInstance(
            typeof(SimpleType), _ => throw new InvalidOperationException("factory failed"), out var failure);

        Assert.Null(created);
        Assert.NotNull(failure);
        Assert.Contains("factory failed", failure);
    }

    [Fact]
    public void CreateInstance_InvokesPerInstanceTeardown()
    {
        var teardownFired = false;

        var created = BenchmarkLifecycle.CreateInstance(typeof(SimpleType),
            _ => new InstanceHandle(new SimpleType(), () => teardownFired = true), out _);

        Assert.NotNull(created);
        created!.Value.InstanceTeardown();
        Assert.True(teardownFired);
    }

    [Fact]
    public void TryRunSetup_Success_ReturnsTrue()
    {
        var flag = false;

        var suite = new BenchmarkSuiteDefinition(
            typeof(SimpleType),
            [],
            _ => { flag = true; });

        var (success, errors) = BenchmarkLifecycle.TryRunSetup(suite, new SimpleType(), MeasurementOptions.Default);

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

        var (success, errors) = BenchmarkLifecycle.TryRunSetup(suite, new SimpleType(), MeasurementOptions.Default);

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

        var (success, errors) = BenchmarkLifecycle.TryRunSetup(suite, new SimpleType(), MeasurementOptions.Default);

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

        await BenchmarkLifecycle.RunTeardown(suite, new SimpleType(), false, () => { }, null);

        Assert.True(teardownCalled);
    }

    [Fact]
    public async Task RunTeardown_DisposesDisposable()
    {
        var disposable = new DisposableSpy();
        var suite = new BenchmarkSuiteDefinition(typeof(DisposableSpy), []);

        await BenchmarkLifecycle.RunTeardown(suite, disposable, false, () => { }, null);

        Assert.True(disposable.Disposed);
    }

    [Fact]
    public async Task RunTeardown_DoesNotDispose_WhenFromFactory()
    {
        var disposable = new DisposableSpy();
        var suite = new BenchmarkSuiteDefinition(typeof(DisposableSpy), []);

        await BenchmarkLifecycle.RunTeardown(suite, disposable, true, () => { }, null);

        Assert.False(disposable.Disposed);
    }

    [Fact]
    public async Task RunTeardown_RunsPostCleanup()
    {
        var cleanupCalled = false;
        var suite = new BenchmarkSuiteDefinition(typeof(SimpleType), []);

        await BenchmarkLifecycle.RunTeardown(suite, new SimpleType(), false, () => { }, () => cleanupCalled = true);

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
            BenchmarkLifecycle.RunTeardown(suite, new SimpleType(), false, () => { }, null));

        Assert.Null(ex);
    }

    [Fact]
    public async Task RunTeardown_AsyncDisposesAsyncDisposable()
    {
        var disposable = new AsyncDisposableSpy();
        var suite = new BenchmarkSuiteDefinition(typeof(AsyncDisposableSpy), []);

        await BenchmarkLifecycle.RunTeardown(suite, disposable, false, () => { }, null);

        Assert.True(disposable.Disposed);
    }

    [Fact]
    public async Task RunTeardown_Invokes_InstanceTeardown_After_ClassTeardown()
    {
        var order = new List<string>();

        var suite = new BenchmarkSuiteDefinition(
            typeof(SimpleType),
            [],
            TeardownDelegate: _ => order.Add("class"));

        await BenchmarkLifecycle.RunTeardown(
            suite,
            new SimpleType(),
            false,
            () => order.Add("instance"),
            null);

        Assert.Equal(["class", "instance"], order);
    }

    [Fact]
    public async Task RunTeardown_InstanceTeardownFailure_DoesNotThrow()
    {
        var suite = new BenchmarkSuiteDefinition(typeof(SimpleType), []);

        var ex = await Record.ExceptionAsync(() =>
            BenchmarkLifecycle.RunTeardown(
                suite,
                new SimpleType(),
                false,
                () => throw new InvalidOperationException("instance teardown failed"),
                null));

        Assert.Null(ex);
    }

    [Fact]
    public void CreateInstance_Propagates_OperationCanceledException()
    {
        Assert.Throws<OperationCanceledException>(() =>
            BenchmarkLifecycle.CreateInstance(typeof(SimpleType),
                _ => throw new OperationCanceledException(), out _));
    }

    [Fact]
    public void TryRunSetup_Propagates_OperationCanceledException()
    {
        var suite = new BenchmarkSuiteDefinition(
            typeof(SimpleType),
            [],
            _ => throw new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() =>
            BenchmarkLifecycle.TryRunSetup(suite, new SimpleType(), MeasurementOptions.Default));
    }

    [Fact]
    public async Task RunTeardown_Propagates_OperationCanceledException()
    {
        var suite = new BenchmarkSuiteDefinition(
            typeof(SimpleType),
            [],
            TeardownDelegate: _ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BenchmarkLifecycle.RunTeardown(suite, new SimpleType(), false, () => { }, null));
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
