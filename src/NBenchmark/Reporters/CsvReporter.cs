using System.Text;

namespace NBenchmark.Reporters;

public sealed class CsvReporter(string outputDirectory = ".", string? name = null, ReportDetail detail = ReportDetail.Simple) : IReporter
{
    private static int _fileCounter;

    private readonly string _outputDirectory = PathValidation.ValidateOutputPath(outputDirectory);

    public string Name => "csv";

    public ReportDetail Detail { get; set; } = detail;

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);

        var fileName = name
                       ?? $"benchmark-results-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Interlocked.Increment(ref _fileCounter):D3}.csv";

        var filePath = Path.Combine(_outputDirectory, fileName);

        var sb = new StringBuilder();
        var detail = Detail.ToString().ToLowerInvariant();

        if (Detail == ReportDetail.Simple)
        {
            sb.AppendLine(
                "Name,Median,Mean,StdDev,StdErr,MarginOfError,CiLower,CiUpper,ConfidenceLevel,CoefficientOfVariation,P95,P99,Ratio,Significant,AllocPerOp,MarginPercent,OutliersRemoved,Detail");
        }
        else
        {
            sb.AppendLine(
                "Name,Median,Mean,StdDev,StdErr,MarginOfError,CiLower,CiUpper,ConfidenceLevel,CoefficientOfVariation,P95,P99,Ratio,Significant,AllocPerOp,MarginPercent,OutliersRemoved,Detail,Q1,Q3,Iqr,LowerFence,UpperFence,Range,N,Skewness,Kurtosis,Mad,AllocMedian,AllocP95,AllocMax,StandardErrorPercent,CoefficientOfVariationPercent,WarmupIterations");
        }

        var table = BenchmarkTable.Build(results);

        foreach (var row in table.Rows)
        {
            var sig = row.SignificanceLabel switch
            {
                "✓" => "true",
                "✗" => "false",
                _ => "",
            };

            var safeName = row.Name.Replace("\"", "\"\"");
            var safeSig = sig.Replace("\"", "\"\"");

            if (Detail == ReportDetail.Simple)
            {
                sb.AppendLine(
                    $"\"{safeName}\"," +
                    $"{row.Median:F1}," +
                    $"{row.Mean:F1}," +
                    $"{row.StandardDeviation:F1}," +
                    $"{row.StandardError:F1}," +
                    $"{row.MarginOfError:F1}," +
                    $"{row.ConfidenceIntervalLower:F1}," +
                    $"{row.ConfidenceIntervalUpper:F1}," +
                    $"{table.ConfidenceLevel:F2}," +
                    $"{row.CoefficientOfVariation:F4}," +
                    $"{row.P95:F1}," +
                    $"{row.P99:F1}," +
                    $"{(double.IsNaN(row.Ratio) ? "null" : $"{row.Ratio:F2}")}," +
                    $"\"{safeSig}\"," +
                    $"{row.MeanAllocatedBytes?.ToString() ?? "null"}," +
                    $"{row.MarginPercent:F2}," +
                    $"{row.OutliersRemoved}," +
                    $"{detail}");
            }
            else
            {
                var lowerFence = row.LowerFence?.ToString("F1") ?? "";
                var upperFence = row.UpperFence?.ToString("F1") ?? "";
                var allocMedian = row.AllocMedian?.ToString() ?? "";
                var allocP95 = row.AllocP95?.ToString() ?? "";
                var allocMax = row.AllocMax?.ToString() ?? "";

                sb.AppendLine(
                    $"\"{safeName}\"," +
                    $"{row.Median:F1}," +
                    $"{row.Mean:F1}," +
                    $"{row.StandardDeviation:F1}," +
                    $"{row.StandardError:F1}," +
                    $"{row.MarginOfError:F1}," +
                    $"{row.ConfidenceIntervalLower:F1}," +
                    $"{row.ConfidenceIntervalUpper:F1}," +
                    $"{table.ConfidenceLevel:F2}," +
                    $"{row.CoefficientOfVariation:F4}," +
                    $"{row.P95:F1}," +
                    $"{row.P99:F1}," +
                    $"{(double.IsNaN(row.Ratio) ? "null" : $"{row.Ratio:F2}")}," +
                    $"\"{safeSig}\"," +
                    $"{row.MeanAllocatedBytes?.ToString() ?? "null"}," +
                    $"{row.MarginPercent:F2}," +
                    $"{row.OutliersRemoved}," +
                    $"{detail}," +
                    $"{row.Q1:F1}," +
                    $"{row.Q3:F1}," +
                    $"{row.InterquartileRange:F1}," +
                    $"\"{lowerFence}\"," +
                    $"\"{upperFence}\"," +
                    $"{row.Range:F1}," +
                    $"{row.N}," +
                    $"{row.Skewness:F4}," +
                    $"{row.Kurtosis:F4}," +
                    $"{row.Mad:F1}," +
                    $"{allocMedian}," +
                    $"{allocP95}," +
                    $"{allocMax}," +
                    $"{row.StandardErrorPercent:F2}," +
                    $"{row.CoefficientOfVariationPercent:F2}," +
                    $"{table.WarmupIterations}");
            }
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
    }
}
