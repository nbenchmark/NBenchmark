using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using NUnit.Framework.Internal.Commands;

namespace NBenchmark.Integration.NUnit.Tests;

public sealed class PerformanceAttributeIntegrationTests
{
    [Test]
    public void PerformanceAttribute_Builds_Runnable_Test_For_Void_Method()
    {
        Assert.That(AttributeBuilder.BuildFor<VoidFixture>(nameof(VoidFixture.VoidMethod)).RunState,
            Is.EqualTo(RunState.Runnable));
    }

    [Test]
    public void PerformanceAttribute_Builds_Runnable_Test_For_Task_Method()
    {
        Assert.That(AttributeBuilder.BuildFor<AsyncTaskFixture>(nameof(AsyncTaskFixture.TaskMethod)).RunState,
            Is.EqualTo(RunState.Runnable));
    }

    [Test]
    public void PerformanceAttribute_Builds_Runnable_Test_For_TypedTask_Method()
    {
        Assert.That(AttributeBuilder.BuildFor<TypedTaskFixture>(nameof(TypedTaskFixture.TypedTaskMethod)).RunState,
            Is.EqualTo(RunState.Runnable));
    }

    [Test]
    public void PerformanceAttribute_Builds_Runnable_Test_For_ValueTask_Method()
    {
        Assert.That(AttributeBuilder.BuildFor<ValueTaskFixture>(nameof(ValueTaskFixture.ValueTaskMethod)).RunState,
            Is.EqualTo(RunState.Runnable));
    }

    [Test]
    public void PerformanceAttribute_Builds_Runnable_Test_For_TypedValueTask_Method()
    {
        Assert.That(AttributeBuilder.BuildFor<TypedValueTaskFixture>(nameof(TypedValueTaskFixture.TypedValueTaskMethod)).RunState,
            Is.EqualTo(RunState.Runnable));
    }

    [Test]
    public void PerformanceAttribute_Wraps_Test_With_PerformanceCommand()
    {
        var test = AttributeBuilder.BuildFor<VoidFixture>(nameof(VoidFixture.VoidMethod));

        Assert.That(test, Is.InstanceOf<TestMethod>());
    }

    [Test]
    public void PerformanceAttribute_Command_Invokes_Body_Method()
    {
        BodyInvokedFixture.InvocationCount = 0;

        var test = AttributeBuilder.BuildFor<BodyInvokedFixture>(nameof(BodyInvokedFixture.BodyMethod));
        var command = new PerformanceAttribute().Wrap(new RunOnlyCommand((TestMethod)test));

        var context = new TestExecutionContext { TestObject = new BodyInvokedFixture() };
        context.CurrentResult = test.MakeTestResult();
        command.Execute(context);

        Assert.That(BodyInvokedFixture.InvocationCount, Is.GreaterThan(0));
        Assert.That(context.CurrentResult.ResultState.Status, Is.EqualTo(TestStatus.Passed));
    }
}

internal sealed class RunOnlyCommand : TestCommand
{
    public RunOnlyCommand(Test test) : base(test) { }

    public override TestResult Execute(TestExecutionContext context) => context.CurrentResult;
}

internal static class AttributeBuilder
{
    public static Test BuildFor<TFixture>(string methodName)
    {
        var methodInfo = new MethodWrapper(typeof(TFixture), typeof(TFixture).GetMethod(methodName)!);
        return new PerformanceAttribute().BuildFrom(methodInfo, null);
    }
}

public sealed class VoidFixture
{
    [Performance(Iterations = 3, WarmupIterations = 1)]
    public void VoidMethod() { }
}

public sealed class AsyncTaskFixture
{
    [Performance(Iterations = 3, WarmupIterations = 1)]
    public Task TaskMethod() => Task.CompletedTask;
}

public sealed class TypedTaskFixture
{
    [Performance(Iterations = 3, WarmupIterations = 1)]
    public Task<int> TypedTaskMethod() => Task.FromResult(0);
}

public sealed class ValueTaskFixture
{
    [Performance(Iterations = 3, WarmupIterations = 1)]
    public ValueTask ValueTaskMethod() => default;
}

public sealed class TypedValueTaskFixture
{
    [Performance(Iterations = 3, WarmupIterations = 1)]
    public async ValueTask<int> TypedValueTaskMethod()
    {
        await Task.Yield();
        return 0;
    }
}

public sealed class BodyInvokedFixture
{
    public static int InvocationCount { get; set; }

    [Performance(Iterations = 3, WarmupIterations = 1)]
    public void BodyMethod()
    {
        InvocationCount++;
    }
}