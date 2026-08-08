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

        var tables = BenchmarkTable.BuildPerClass(results);
        var profile = tables[0].Profile.ToString().ToLowerInvariant();

        var percentileCols = tables
            .SelectMany(t => t.Rows)
            .SelectMany(r => r.Percentiles)
            .Select(e => e.Percentile)
            .Where(p => p > 0.50 && p < 1.0)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        var percentileHeaders = string.Join(",", percentileCols.Select(p => $"P{BenchmarkTable.FormatPercentileKey(p)}"));
        var percentileHeaderPart = percentileHeaders.Length > 0 ? $",{percentileHeaders}" : "";

        var baseHeaders = "ClassName,Name,Median";

        if (Detail == ReportDetail.Simple)
        {
            baseHeaders += ",OpsPerSecond";

            sb.AppendLine(
                $"{baseHeaders},Ratio,Significant,AllocPerOp,Gen0,Gen1,Gen2,SchemaVersion,MeasurementEpoch,Detail,Profile,RuntimeProfile,RuntimeKnobs,Isolation");
        }
        else if (Detail == ReportDetail.Standard)
        {
            baseHeaders += ",Mean,OpsPerSecond";

            sb.AppendLine(
                $"{baseHeaders}{percentileHeaderPart},StdDev,StdErr,MarginOfError,CiLower,CiUpper,ConfidenceLevel,CoefficientOfVariation,Ratio,RatioCiLower,RatioCiUpper,RatioReplicates,Significant,EffectMetric,EffectValue,Magnitude,AllocPerOp,Gen0,Gen1,Gen2,MarginPercent,OutliersRemoved,SchemaVersion,MeasurementEpoch,Detail,Profile,RuntimeProfile,RuntimeKnobs,Isolation");
        }
        else
        {
            baseHeaders += ",Mean,OpsPerSecond";

            sb.AppendLine(
                $"{baseHeaders}{percentileHeaderPart},StdDev,StdErr,MarginOfError,CiLower,CiUpper,ConfidenceLevel,CoefficientOfVariation,Ratio,RatioCiLower,RatioCiUpper,RatioReplicates,Significant,EffectMetric,EffectValue,Magnitude,AllocPerOp,Gen0,Gen1,Gen2,MarginPercent,OutliersRemoved,SchemaVersion,MeasurementEpoch,Detail,Profile,RuntimeProfile,RuntimeKnobs,Q1,Q3,Iqr,LowerFence,UpperFence,Range,N,Skewness,Kurtosis,Mad,AllocMedian,AllocP95,AllocMax,StandardErrorPercent,CoefficientOfVariationPercent,WarmupIterations,AutoTuneWarmup,AutoTuneSamples,AutoTuneOpsPerSample,AutoTuneSampleStop,AutoTuneCiWidth,AutoTuneTuningMs,AutoTuneJitterMetric,AutoTuneDetectorSwitched,AutoTuneSplitHalfDrift,AutoTuneRestarts,AutoTuneWarmupTimeFloorMet,AutoTuneWarmupJitMethods,HeapCommitted,HeapFragmented,ExceptionPerOp,CpuTimeNsPerOp,CpuWallRatio,DiagnosticsMode,Categories,Isolation");
        }

        foreach (var table in tables)
        foreach (var row in table.Rows)
        {
            var sig = row.SignificanceLabel switch
            {
                "✓" => "true",
                "✗" => "false",
                _ => "",
            };

            var safeClassName = row.ClassName.Replace("\"", "\"\"");
            var safeName = row.Name.Replace("\"", "\"\"");
            var safeSig = sig.Replace("\"", "\"\"");
            var safeEffectMetric = (row.Effect?.Metric ?? string.Empty).Replace("\"", "\"\"");
            var safeMagnitude = (row.Effect?.Magnitude ?? string.Empty).Replace("\"", "\"\"");

            // Where the row was measured. Present at every detail level, because a CSV-driven
            // dashboard had no way at all to tell a clean-room row from a host one - JSON carries the
            // whole record and Markdown renders a column, but CSV emitted neither, so the one format
            // built for automated trend-tracking was the one that could silently plot the two together.
            var safeIsolation = row.IsolationStatus.ToLabel().Replace("\"", "\"\"");

            var percentileValues = string.Join(",", percentileCols
                .Select(p => row.GetPercentile(p)?.ToString("F1") ?? ""));

            var percentileData = percentileValues.Length > 0 ? $",{percentileValues}" : "";

            var commonData = $"\"{safeClassName}\"," +
                             $"\"{safeName}\"," +
                             $"{row.Median:F1}";

            if (Detail == ReportDetail.Simple)
            {
                var simpleDiag = row.Diagnostics;

                sb.AppendLine(
                    $"{commonData},{row.OperationsPerSecond:F1}," +
                    $"{(double.IsNaN(row.Ratio) ? "null" : $"{row.Ratio:F2}")}," +
                    $"\"{safeSig}\"," +
                    $"{row.MeanAllocatedBytes?.ToString() ?? "null"}," +
                    $"{simpleDiag?.Gen0Collections?.ToString() ?? ""}," +
                    $"{simpleDiag?.Gen1Collections?.ToString() ?? ""}," +
                    $"{simpleDiag?.Gen2Collections?.ToString() ?? ""}," +
                    $"{ReportFormat.SchemaVersion},{ReportFormat.MeasurementEpoch}," +
                    $"{detail}," +
                    $"{profile}," +
                    $"{table.RuntimeProfileName},\"{table.RuntimeKnobs}\",\"{safeIsolation}\"");
            }
            else
            {
                var diag = row.Diagnostics;
                var diagCols = $"{diag?.Gen0Collections?.ToString() ?? ""},{diag?.Gen1Collections?.ToString() ?? ""},{diag?.Gen2Collections?.ToString() ?? ""}";

                var fullData = $"{commonData},{row.Mean:F1},{row.OperationsPerSecond:F1}{percentileData}," +
                               $"{row.StandardDeviation:F1}," +
                               $"{row.StandardError:F1}," +
                               $"{row.MarginOfError:F1}," +
                               $"{row.ConfidenceIntervalLower:F1}," +
                               $"{row.ConfidenceIntervalUpper:F1}," +
                               $"{table.ConfidenceLevel:F2}," +
                               $"{row.CoefficientOfVariation:F4}," +
                               $"{(double.IsNaN(row.Ratio) ? "null" : $"{row.Ratio:F2}")}," +

                               // Empty rather than null when the run had a single launch: there is no
                               // interval to report, which is different from one that could not be
                               // computed. A trend consumer reading a blank knows not to plot it.
                               $"{row.RatioEstimate?.Lower.ToString("F3") ?? ""}," +
                               $"{row.RatioEstimate?.Upper.ToString("F3") ?? ""}," +
                               $"{row.RatioEstimate?.Replicates.ToString() ?? ""}," +
                               $"\"{safeSig}\"," +
                               $"\"{safeEffectMetric}\"," +
                               $"{row.Effect?.Value?.ToString("F4") ?? ""}," +
                               $"\"{safeMagnitude}\"," +
                               $"{row.MeanAllocatedBytes?.ToString() ?? "null"}," +
                               $"{diagCols}," +
                               $"{row.MarginPercent:F2}," +
                               $"{row.OutliersRemoved}," +
                               $"{ReportFormat.SchemaVersion},{ReportFormat.MeasurementEpoch}," +
                               $"{detail}," +
                               $"{profile}," +
                               $"{table.RuntimeProfileName},\"{table.RuntimeKnobs}\"";

                if (Detail == ReportDetail.Standard)
                    sb.AppendLine($"{fullData},\"{safeIsolation}\"");
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

                    var atJitterMetric = autoTune?.JitterMetric.HasValue == true
                        ? autoTune.JitterMetric.Value.ToString("F4")
                        : "";

                    var atDetectorSwitched = autoTune?.OutlierDetectorSwitched == true ? "true" : "";
                    var atSplitHalfDrift = autoTune is null ? "" : autoTune.SplitHalfDrift.ToString("F4");
                    var atRestarts = autoTune?.MeasurementRestarts.ToString() ?? "";
                    var atWarmupFloorMet = autoTune is null ? "" : autoTune.WarmupTimeFloorMet ? "true" : "false";
                    var atWarmupJitMethods = autoTune?.WarmupJitCompiledMethods.ToString() ?? "";

                    var safeCategories = string.Join("; ", row.Categories).Replace("\"", "\"\"");
                    var advancedDiag = row.Diagnostics;
                    var heapCommitted = advancedDiag?.HeapCommittedBytes?.ToString() ?? "";
                    var heapFragmented = advancedDiag?.HeapFragmentedBytes?.ToString() ?? "";
                    var excPerOp = advancedDiag?.ExceptionCountPerOp?.ToString("F4") ?? "";
                    var cpuNs = advancedDiag?.CpuTimeNsPerOp?.ToString("F1") ?? "";
                    var cpuRatio = advancedDiag?.CpuWallRatio?.ToString("F4") ?? "";
                    var diagMode = (advancedDiag?.Mode.ToString() ?? "").Replace("\"", "\"\"");

                    sb.AppendLine(
                        $"{fullData}," +
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
                        $"{atJitterMetric}," +
                        $"{atDetectorSwitched}," +
                        $"{atSplitHalfDrift}," +
                        $"{atRestarts}," +
                        $"{atWarmupFloorMet}," +
                        $"{atWarmupJitMethods}," +
                        $"{heapCommitted}," +
                        $"{heapFragmented}," +
                        $"{excPerOp}," +
                        $"{cpuNs}," +
                        $"{cpuRatio}," +
                        $"\"{diagMode}\"," +
                        $"\"{safeCategories}\"," +
                        $"\"{safeIsolation}\"");
                }
            }
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
    }
}
