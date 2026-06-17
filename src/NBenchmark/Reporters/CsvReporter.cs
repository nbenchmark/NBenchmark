using System.Text;
using NBenchmark.Stats;

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
                "Name,Median,Mean,StdDev,StdErr,MarginOfError,CiLower,CiUpper,ConfidenceLevel,CoefficientOfVariation,P95,P99,Ratio,Significant,EffectMetric,EffectValue,Magnitude,AllocPerOp,MarginPercent,OutliersRemoved,Detail,Profile");
        }
        else
        {
            sb.AppendLine(
                "Name,Median,Mean,StdDev,StdErr,MarginOfError,CiLower,CiUpper,ConfidenceLevel,CoefficientOfVariation,P95,P99,Ratio,Significant,EffectMetric,EffectValue,Magnitude,AllocPerOp,MarginPercent,OutliersRemoved,Detail,Profile,Q1,Q3,Iqr,LowerFence,UpperFence,Range,N,Skewness,Kurtosis,Mad,AllocMedian,AllocP95,AllocMax,StandardErrorPercent,CoefficientOfVariationPercent,WarmupIterations,AutoTuneWarmup,AutoTuneSamples,AutoTuneOpsPerSample,AutoTuneSampleStop,AutoTuneCiWidth,AutoTuneTuningMs,Categories");
        }

        var table = BenchmarkTable.Build(results);
        var profile = table.Profile.ToString().ToLowerInvariant();

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
            var safeEffectMetric = (row.Effect?.Metric ?? string.Empty).Replace("\"", "\"\"");
            var safeMagnitude = (row.Effect?.Magnitude ?? string.Empty).Replace("\"", "\"\"");

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
                    $"\"{safeEffectMetric}\"," +
                    $"{row.Effect?.Value?.ToString("F4") ?? ""}," +
                    $"\"{safeMagnitude}\"," +
                    $"{row.MeanAllocatedBytes?.ToString() ?? "null"}," +
                    $"{row.MarginPercent:F2}," +
                    $"{row.OutliersRemoved}," +
                    $"{detail}," +
                    $"{profile}");
            }
            else
            {
                var lowerFence = row.LowerFence?.ToString("F1") ?? "";
                var upperFence = row.UpperFence?.ToString("F1") ?? "";
                var allocMedian = row.AllocMedian?.ToString() ?? "";
                var allocP95 = row.AllocP95?.ToString() ?? "";
                var allocMax = row.AllocMax?.ToString() ?? "";

                var autoTune = row.AutoTune;
                var atWarmup = autoTune?.ResolvedWarmup.ToString() ?? "";
                var atSamples = autoTune?.ResolvedSamples.ToString() ?? "";
                var atOps = autoTune?.OpsPerSample.ToString() ?? "";
                var atSampleStop = autoTune?.SampleStop.ToString() ?? "";
                var atCiWidth = autoTune is null ? "" : autoTune.AchievedRelativeCiWidth.ToString("F4");
                var atTuningMs = autoTune is null ? "" : autoTune.TuningWallClock.TotalMilliseconds.ToString("F1");

                var safeCategories = string.Join("; ", row.Categories).Replace("\"", "\"\"");

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
                    $"\"{safeEffectMetric}\"," +
                    $"{row.Effect?.Value?.ToString("F4") ?? ""}," +
                    $"\"{safeMagnitude}\"," +
                    $"{row.MeanAllocatedBytes?.ToString() ?? "null"}," +
                    $"{row.MarginPercent:F2}," +
                    $"{row.OutliersRemoved}," +
                    $"{detail}," +
                    $"{profile}," +
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
                    $"{table.WarmupIterations}," +
                    $"{atWarmup}," +
                    $"{atSamples}," +
                    $"{atOps}," +
                    $"{atSampleStop}," +
                    $"{atCiWidth}," +
                    $"{atTuningMs}," +
                    $"\"{safeCategories}\"");
            }
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
    }
}
