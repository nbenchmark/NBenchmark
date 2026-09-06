using Xunit;
using Xunit.Abstractions;

namespace NBenchmark.Integration.xUnit.Tests;

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
        Assert.Null(parsed.ReferenceMethod);
        Assert.Equal(0, parsed.MaxSlowdownRatio);
        Assert.Equal(0, parsed.Samples);
        Assert.Equal(0, parsed.WarmupSamples);
        Assert.False(parsed.MeasureAllocations);
        Assert.Equal(OutlierMode.IqrFence, parsed.OutlierMode);
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
            ReferenceMethod = "ReferenceMethod",
            MaxSlowdownRatio = 1.5,
            Samples = 42,
            WarmupSamples = 7,
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
        Assert.Equal("ReferenceMethod", parsed.ReferenceMethod);
        Assert.Equal(1.5, parsed.MaxSlowdownRatio);
        Assert.Equal(42, parsed.Samples);
        Assert.Equal(7, parsed.WarmupSamples);
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
        Assert.Null(parsed.ReferenceMethod);
        Assert.Equal(0, parsed.MaxSlowdownRatio);
        Assert.Equal(0, parsed.Samples);
        Assert.Equal(0, parsed.WarmupSamples);
        Assert.False(parsed.MeasureAllocations);
        Assert.Equal(OutlierMode.IqrFence, parsed.OutlierMode);
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
            [nameof(PerformanceFactAttribute.ReferenceMethod)] = "MyReference",
            [nameof(PerformanceFactAttribute.MaxSlowdownRatio)] = 1.8d,
            [nameof(PerformanceFactAttribute.Samples)] = 64,
            [nameof(PerformanceFactAttribute.WarmupSamples)] = 8,
            [nameof(PerformanceFactAttribute.MeasureAllocations)] = true,
            [nameof(PerformanceFactAttribute.OutlierMode)] = OutlierMode.IqrFence,
            [nameof(PerformanceFactAttribute.ConfidenceLevel)] = 0.99,
        });

        var parsed = PerformanceAttributeParser.Parse(attributeInfo);

        Assert.Equal(250, parsed.MaxMeanNs);
        Assert.Equal(400, parsed.MaxP95Ns);
        Assert.Equal(4096, parsed.MaxAllocatedBytes);
        Assert.Equal("MyReference", parsed.ReferenceMethod);
        Assert.Equal(1.8, parsed.MaxSlowdownRatio);
        Assert.Equal(64, parsed.Samples);
        Assert.Equal(8, parsed.WarmupSamples);
        Assert.True(parsed.MeasureAllocations);
        Assert.Equal(OutlierMode.IqrFence, parsed.OutlierMode);
        Assert.Equal(0.99, parsed.ConfidenceLevel);
    }

    /// <summary>
    ///     <c>LaunchCount</c> survives both parse paths, and an unset one reads as one launch.
    /// </summary>
    /// <remarks>
    ///     Worth its own test because this parser is a field-by-field copy, and the failure mode of a
    ///     field-by-field copy is silence: a test asking for three replicates would be measured once, its
    ///     ratio gate would keep comparing unpaired numbers, and nothing anywhere would say so.
    /// </remarks>
    [Theory]
    [InlineData(3, 3)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    public void Parse_Carries_LaunchCount_From_The_Runtime_Attribute(int declared, int expected)
    {
        var attributeInfo = new RuntimeBackedAttributeInfo(
            new PerformanceFactAttribute { LaunchCount = declared });

        Assert.Equal(expected, PerformanceAttributeParser.Parse(attributeInfo).LaunchCount);
    }

    [Fact]
    public void Parse_Carries_LaunchCount_From_A_Named_Argument()
    {
        var attributeInfo = new NamedOnlyAttributeInfo(new Dictionary<string, object?>
        {
            [nameof(PerformanceFactAttribute.LaunchCount)] = 2,
        });

        Assert.Equal(2, PerformanceAttributeParser.Parse(attributeInfo).LaunchCount);
    }

    [Fact]
    public void Parse_Defaults_LaunchCount_To_One_When_Unset()
        => Assert.Equal(1, PerformanceAttributeParser.Parse(new NamedOnlyAttributeInfo()).LaunchCount);

    /// <summary>
    ///     And it survives serialization, which is how a test case reaches the runner in xUnit.
    /// </summary>
    [Fact]
    public void PerformanceTestData_RoundTrips_LaunchCount()
    {
        var data = PerformanceTestData.FromThresholds(new PerformanceFactAttribute { LaunchCount = 3 });

        Assert.Equal(3, data.LaunchCount);

        var info = new RecordingSerializationInfo();
        data.Serialize(info);

        var revived = (PerformanceTestData)Activator.CreateInstance(
            typeof(PerformanceTestData), nonPublic: true)!;

        revived.Deserialize(info);

        Assert.Equal(3, revived.LaunchCount);
    }

    /// <summary>
    ///     A test case serialized by an earlier build carries no <c>LaunchCount</c> at all, and must
    ///     revive as one launch rather than as zero - which is not a valid replicate count.
    /// </summary>
    [Fact]
    public void PerformanceTestData_Revives_A_Missing_LaunchCount_As_One()
    {
        var revived = (PerformanceTestData)Activator.CreateInstance(
            typeof(PerformanceTestData), nonPublic: true)!;

        revived.Deserialize(new RecordingSerializationInfo());

        Assert.Equal(1, revived.LaunchCount);
    }

    /// <summary>
    ///     A minimal <see cref="IXunitSerializationInfo" /> that keeps values in a dictionary, so a
    ///     round-trip can be asserted without standing up xUnit's own serializer.
    /// </summary>
    private sealed class RecordingSerializationInfo : IXunitSerializationInfo
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public void AddValue(string key, object? value, Type? type = null) => _values[key] = value;

        public object? GetValue(string key, Type type)
        {
            if (_values.TryGetValue(key, out var value))
                return value;

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        public T GetValue<T>(string key) => (T)GetValue(key, typeof(T))!;
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

        public IEnumerable<object> GetConstructorArguments() => [];

        public IEnumerable<IAttributeInfo> GetCustomAttributes(string assemblyQualifiedAttributeTypeName) => [];

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
