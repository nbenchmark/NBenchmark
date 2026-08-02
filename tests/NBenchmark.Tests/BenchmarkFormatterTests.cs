using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkFormatterTests
{
    [Theory]
    [InlineData(0.5, "0.5 ns")]
    [InlineData(999.9, "999.9 ns")]
    [InlineData(1000, "1.00 µs")]
    [InlineData(1500, "1.50 µs")]
    [InlineData(500_000, "500.00 µs")]
    [InlineData(1_000_000, "1.00 ms")]
    [InlineData(1_500_000, "1.50 ms")]
    [InlineData(500_000_000, "500.00 ms")]
    [InlineData(1_000_000_000, "1.00 s")]
    public void FormatNs_Formats_Correctly(double ns, string expected)
    {
        var result = BenchmarkFormatter.FormatNs(ns);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KiB")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(1048575, "1024.0 KiB")]
    [InlineData(1048576, "1.0 MiB")]
    [InlineData(1572864, "1.5 MiB")]
    public void FormatBytes_Formats_Correctly(long bytes, string expected)
    {
        var result = BenchmarkFormatter.FormatBytes(bytes);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(double.NaN, "-")]
    [InlineData(0.5, "0.5 ops/s")]
    [InlineData(999.9, "999.9 ops/s")]
    [InlineData(1000, "1.00 Kops/s")]
    [InlineData(1500, "1.50 Kops/s")]
    [InlineData(500_000, "500.00 Kops/s")]
    [InlineData(1_000_000, "1.00 Mops/s")]
    [InlineData(1_500_000, "1.50 Mops/s")]
    [InlineData(500_000_000, "500.00 Mops/s")]
    [InlineData(1_000_000_000, "1.00 Gops/s")]
    [InlineData(1_500_000_000, "1.50 Gops/s")]
    [InlineData(500_000_000_000.0, "500.00 Gops/s")]
    [InlineData(1_000_000_000_000.0, "1.00 Tops/s")]
    public void FormatOpsPerSecond_Formats_Correctly(double opsPerSecond, string expected)
    {
        var result = BenchmarkFormatter.FormatOpsPerSecond(opsPerSecond);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatOpsPerSecond_Zero_Formats_As_Ops_Per_Second() => Assert.Equal("0.0 ops/s", BenchmarkFormatter.FormatOpsPerSecond(0));
}
