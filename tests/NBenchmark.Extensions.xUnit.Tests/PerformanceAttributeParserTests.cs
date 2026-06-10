using NBenchmark.Extensions.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace NBenchmark.Extensions.xUnit.Tests;

public sealed class PerformanceAttributeParserTests
{
    [Fact]
    public void Parse_Uses_Runtime_Attribute_Defaults_When_Named_Arguments_Are_Unset()
    {
        var attributeInfo = new RuntimeBackedAttributeInfo(new PerformanceFactAttribute());

        var parsed = PerformanceAttributeParser.Parse(attributeInfo);

        Assert.Equal(-1, parsed.MaxMeanNs);
        Assert.Equal(-1, parsed.MaxP95Ns);
        Assert.Equal(-1, parsed.MaxAllocatedBytes);
        Assert.Null(parsed.BaselinePath);
        Assert.Equal(1.2, parsed.MaxSlowdownRatio);
        Assert.Equal(0, parsed.Iterations);
        Assert.Equal(0, parsed.WarmupIterations);
        Assert.False(parsed.MeasureAllocations);
        Assert.Equal(OutlierMode.RemoveTop5Percent, parsed.OutlierMode);
        Assert.Equal(0.95, parsed.ConfidenceLevel);
    }

    [Fact]
    public void Parse_Prefers_Runtime_Attribute_Values_Over_Named_Argument_Defaults()
    {
        var runtimeAttribute = new PerformanceFactAttribute
        {
            MaxMeanNs = 123,
            MaxP95Ns = 456,
            MaxAllocatedBytes = 1024,
            BaselinePath = "baseline.json",
            MaxSlowdownRatio = 1.5,
            Iterations = 42,
            WarmupIterations = 7,
            MeasureAllocations = true,
            OutlierMode = OutlierMode.None,
            ConfidenceLevel = 0.9,
        };

        var attributeInfo = new RuntimeBackedAttributeInfo(
            runtimeAttribute,
            new Dictionary<string, object?>
            {
                [nameof(PerformanceFactAttribute.MaxMeanNs)] = 0d,
                [nameof(PerformanceFactAttribute.MaxP95Ns)] = 0d,
                [nameof(PerformanceFactAttribute.MaxAllocatedBytes)] = 0L,
                [nameof(PerformanceFactAttribute.MaxSlowdownRatio)] = 0d,
                [nameof(PerformanceFactAttribute.OutlierMode)] = OutlierMode.RemoveTop5Percent,
                [nameof(PerformanceFactAttribute.ConfidenceLevel)] = 0d,
            });

        var parsed = PerformanceAttributeParser.Parse(attributeInfo);

        Assert.Equal(123, parsed.MaxMeanNs);
        Assert.Equal(456, parsed.MaxP95Ns);
        Assert.Equal(1024, parsed.MaxAllocatedBytes);
        Assert.Equal("baseline.json", parsed.BaselinePath);
        Assert.Equal(1.5, parsed.MaxSlowdownRatio);
        Assert.Equal(42, parsed.Iterations);
        Assert.Equal(7, parsed.WarmupIterations);
        Assert.True(parsed.MeasureAllocations);
        Assert.Equal(OutlierMode.None, parsed.OutlierMode);
        Assert.Equal(0.9, parsed.ConfidenceLevel);
    }

    [Fact]
    public void Parse_Normalizes_Fallback_Defaults_When_Runtime_Attribute_Is_Unavailable()
    {
        var attributeInfo = new NamedOnlyAttributeInfo();

        var parsed = PerformanceAttributeParser.Parse(attributeInfo);

        Assert.Equal(-1, parsed.MaxMeanNs);
        Assert.Equal(-1, parsed.MaxP95Ns);
        Assert.Equal(-1, parsed.MaxAllocatedBytes);
        Assert.Null(parsed.BaselinePath);
        Assert.Equal(1.2, parsed.MaxSlowdownRatio);
        Assert.Equal(0, parsed.Iterations);
        Assert.Equal(0, parsed.WarmupIterations);
        Assert.False(parsed.MeasureAllocations);
        Assert.Equal(OutlierMode.RemoveTop5Percent, parsed.OutlierMode);
        Assert.Equal(0.95, parsed.ConfidenceLevel);
    }

    [Fact]
    public void Parse_Uses_Fallback_Named_Argument_Values_When_Provided()
    {
        var attributeInfo = new NamedOnlyAttributeInfo(new Dictionary<string, object?>
        {
            [nameof(PerformanceFactAttribute.MaxMeanNs)] = 250d,
            [nameof(PerformanceFactAttribute.MaxP95Ns)] = 400d,
            [nameof(PerformanceFactAttribute.MaxAllocatedBytes)] = 4096L,
            [nameof(PerformanceFactAttribute.BaselinePath)] = "my-baseline.json",
            [nameof(PerformanceFactAttribute.MaxSlowdownRatio)] = 1.8d,
            [nameof(PerformanceFactAttribute.Iterations)] = 64,
            [nameof(PerformanceFactAttribute.WarmupIterations)] = 8,
            [nameof(PerformanceFactAttribute.MeasureAllocations)] = true,
            [nameof(PerformanceFactAttribute.OutlierMode)] = OutlierMode.IqrFence,
            [nameof(PerformanceFactAttribute.ConfidenceLevel)] = 0.99,
        });

        var parsed = PerformanceAttributeParser.Parse(attributeInfo);

        Assert.Equal(250, parsed.MaxMeanNs);
        Assert.Equal(400, parsed.MaxP95Ns);
        Assert.Equal(4096, parsed.MaxAllocatedBytes);
        Assert.Equal("my-baseline.json", parsed.BaselinePath);
        Assert.Equal(1.8, parsed.MaxSlowdownRatio);
        Assert.Equal(64, parsed.Iterations);
        Assert.Equal(8, parsed.WarmupIterations);
        Assert.True(parsed.MeasureAllocations);
        Assert.Equal(OutlierMode.IqrFence, parsed.OutlierMode);
        Assert.Equal(0.99, parsed.ConfidenceLevel);
    }

    private sealed class RuntimeBackedAttributeInfo : NamedOnlyAttributeInfo, IReflectionAttributeInfo
    {
        public RuntimeBackedAttributeInfo(
            Attribute attribute,
            IReadOnlyDictionary<string, object?>? namedArguments = null)
            : base(namedArguments)
        {
            Attribute = attribute;
        }

        public Attribute Attribute { get; }
    }

    private class NamedOnlyAttributeInfo : LongLivedMarshalByRefObject, IAttributeInfo
    {
        private readonly IReadOnlyDictionary<string, object?> _namedArguments;

        public NamedOnlyAttributeInfo(IReadOnlyDictionary<string, object?>? namedArguments = null)
        {
            _namedArguments = namedArguments ?? new Dictionary<string, object?>();
        }

        public IEnumerable<object> GetConstructorArguments()
        {
            return [];
        }

        public IEnumerable<IAttributeInfo> GetCustomAttributes(string assemblyQualifiedAttributeTypeName)
        {
            return [];
        }

        public TValue GetNamedArgument<TValue>(string argumentName)
        {
            if (!_namedArguments.TryGetValue(argumentName, out var value))
                return default!;

            if (value is null)
                return default!;

            return (TValue)value;
        }
    }
}
