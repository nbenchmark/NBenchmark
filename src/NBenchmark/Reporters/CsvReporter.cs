using System.Text;

namespace NBenchmark.Reporters;

public sealed class CsvReporter : IReporter
{
    private readonly string _outputPath;

    public CsvReporter(string outputPath = "benchmark-results.csv")
    {
        _outputPath = PathValidation.ValidateOutputPath(outputPath);
    }

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            "Name,Median,Mean,StdDev,StdErr,MarginOfError,CiLower,CiUpper,ConfidenceLevel,CoefficientOfVariation,P95,P99,Ratio,Significant,AllocPerOp");

        var multiBenchmark = results.Count > 1;
        var successful = results.Where(r => !r.Errored).ToList();

        var baseline = successful.Count > 0
            ? successful.FirstOrDefault(r => r.IsBaseline) ?? successful.MinBy(r => r.Median)!
            : null;

        foreach (var result in results.OrderBy(r => r.Median))
        {
            var ratio = result.Errored || baseline is null || baseline.Median == 0
                ? double.NaN
                : result.Median / baseline.Median;

            var sig = !multiBenchmark || result.IsBaseline || !result.IsSignificant.HasValue
                ? ""
                : result.IsSignificant.Value
                    ? "true"
                    : "false";

            var safeName = result.Name.Replace("\"", "\"\"");
            var safeSig = sig.Replace("\"", "\"\"");

            sb.AppendLine(
                $"\"{safeName}\"," +
                $"{result.Median:F1}," +
                $"{result.Mean:F1}," +
                $"{result.StandardDeviation:F1}," +
                $"{result.StandardError:F1}," +
                $"{result.MarginOfError:F1}," +
                $"{result.ConfidenceIntervalLower:F1}," +
                $"{result.ConfidenceIntervalUpper:F1}," +
                $"{result.ConfidenceLevel:F2}," +
                $"{result.CoefficientOfVariation:F4}," +
                $"{result.P95:F1}," +
                $"{result.P99:F1}," +
                $"{(double.IsNaN(ratio) ? "null" : $"{ratio:F2}")}," +
                $"\"{safeSig}\"," +
                $"{result.MeanAllocatedBytes?.ToString() ?? "null"}"
            );
        }

        await File.WriteAllTextAsync(_outputPath, sb.ToString(), cancellationToken);
    }
}