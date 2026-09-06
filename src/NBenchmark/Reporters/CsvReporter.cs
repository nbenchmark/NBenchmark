using System.Text;

namespace NBenchmark.Reporters;

public sealed class CsvReporter(string outputDirectory = ".", string? fileName = null, ReportDetail detail = ReportDetail.Simple) : IReporter
{
    private static int _fileCounter;

    private readonly string _outputDirectory = PathValidation.ValidateOutputPath(outputDirectory);

    public string Name => "csv";

    public ReportDetail Detail { get; set; } = detail;

    /// <summary>
    ///     The path of the file the last <see cref="ReportAsync" /> wrote.
    /// </summary>
    /// <remarks>
    ///     Internal: the extension methods that wrap this reporter for a single result return the path
    ///     they wrote, and the name is generated inside <see cref="ReportAsync" /> from a timestamp and
    ///     a counter, so it cannot be predicted from outside.
    /// </remarks>
    internal string? LastWrittenPath { get; private set; }

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);

        var resolvedName = fileName
                       ?? $"benchmark-results-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Interlocked.Increment(ref _fileCounter):D3}.csv";

        var filePath = Path.Combine(_outputDirectory, resolvedName);
        LastWrittenPath = filePath;

        var sb = new StringBuilder();
        var detail = Detail.ToString().ToLowerInvariant();

        var tables = BenchmarkTable.BuildPerClass(results);
        var profile = tables[0].GcBehavior.ToString().ToLowerInvariant();

        var percentileCols = tables
            .SelectMany(t => t.Rows)
            .SelectMany(r => r.Result.Percentiles)
            .Select(e => e.Percentile)
            .Where(p => p > 0.50 && p < 1.0)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        var percentileHeaders = string.Join(",", percentileCols.Select(p => $"P{BenchmarkTable.FormatPercentileKey(p)}"));
        var percentileHeaderPart = percentileHeaders.Length > 0 ? $",{percentileHeaders}" : "";

        var baseHeaders = "ClassName,Name,MedianNs";

        if (Detail == ReportDetail.Simple)
        {
            baseHeaders += ",OpsPerSecond";

            sb.AppendLine(
                $"{baseHeaders},Ratio,Significant,AllocPerOp,Gen0,Gen1,Gen2,SchemaVersion,MeasurementEpoch,Detail,GcBehavior,RuntimeProfile,RuntimeKnobs,ThreadControl,InterferenceFilter,Isolation");
        }
        else if (Detail == ReportDetail.Standard)
        {
            baseHeaders += ",MeanNs,OpsPerSecond";

            sb.AppendLine(
                $"{baseHeaders}{percentileHeaderPart},StdDev,StdErr,MarginOfErrorNs,CiLower,CiUpper,ConfidenceLevel,CoefficientOfVariation,Ratio,RatioCiLower,RatioCiUpper,RatioReplicates,Significant,EffectMetric,EffectValue,Magnitude,AllocPerOp,Gen0,Gen1,Gen2,MarginOfErrorPercent,OutliersRemoved,SchemaVersion,MeasurementEpoch,Detail,GcBehavior,RuntimeProfile,RuntimeKnobs,ThreadControl,InterferenceFilter,Isolation");
        }
        else
        {
            baseHeaders += ",MeanNs,OpsPerSecond";

            sb.AppendLine(
                $"{baseHeaders}{percentileHeaderPart},StdDev,StdErr,MarginOfErrorNs,CiLower,CiUpper,ConfidenceLevel,CoefficientOfVariation,Ratio,RatioCiLower,RatioCiUpper,RatioReplicates,Significant,EffectMetric,EffectValue,Magnitude,AllocPerOp,Gen0,Gen1,Gen2,MarginOfErrorPercent,OutliersRemoved,SchemaVersion,MeasurementEpoch,Detail,GcBehavior,RuntimeProfile,RuntimeKnobs,ThreadControl,InterferenceFilter,Q1Ns,Q3Ns,Iqr,LowerFenceNs,UpperFenceNs,RangeNs,SampleCount,Skewness,Kurtosis,MedianAbsoluteDeviationNs,AllocatedBytesMedian,AllocatedBytesP95,AllocatedBytesMax,StandardErrorPercent,CoefficientOfVariationPercent,WarmupSamples,AutoTuneWarmup,AutoTuneSamples,AutoTuneOpsPerSample,AutoTuneSampleStop,AutoTuneCiWidth,AutoTuneTuningMs,AutoTuneJitterMetric,AutoTuneDetectorSwitched,AutoTuneSplitHalfDrift,AutoTuneRestarts,AutoTuneWarmupTimeFloorMet,AutoTuneWarmupJitMethods,HeapCommitted,HeapFragmented,ExceptionPerOp,CpuTimeNsPerOp,CpuWallRatio,DiagnosticsMode,Categories,Isolation");
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

            var safeClassName = row.Result.ClassName.Replace("\"", "\"\"");
            var safeName = row.Result.Name.Replace("\"", "\"\"");
            var safeSig = sig.Replace("\"", "\"\"");
            var safeEffectMetric = (row.Result.Effect?.Metric ?? string.Empty).Replace("\"", "\"\"");
            var safeMagnitude = (row.Result.Effect?.Magnitude ?? string.Empty).Replace("\"", "\"\"");

            // Where the row was measured. Present at every detail level, because a CSV-driven
            // dashboard had no way at all to tell a clean-room row from a host one - JSON carries the
            // whole record and Markdown renders a column, but CSV emitted neither, so the one format
            // built for automated trend-tracking was the one that could silently plot the two together.
            var safeIsolation = row.Result.IsolationStatus.ToLabel().Replace("\"", "\"\"");

            var percentileValues = string.Join(",", percentileCols
                .Select(p => row.GetPercentile(p)?.ToString("F1") ?? ""));

            var percentileData = percentileValues.Length > 0 ? $",{percentileValues}" : "";

            var commonData = $"\"{safeClassName}\"," +
                             $"\"{safeName}\"," +
                             $"{row.Result.MedianNs:F1}";

            if (Detail == ReportDetail.Simple)
            {
                var simpleDiag = row.Result.Diagnostics;

                sb.AppendLine(
                    $"{commonData},{row.Result.OperationsPerSecond:F1}," +
                    $"{(double.IsNaN(row.Ratio) ? "null" : $"{row.Ratio:F2}")}," +
                    $"\"{safeSig}\"," +
                    $"{row.Result.AllocatedBytesMean?.ToString() ?? "null"}," +
                    $"{simpleDiag?.Gen0Collections?.ToString() ?? ""}," +
                    $"{simpleDiag?.Gen1Collections?.ToString() ?? ""}," +
                    $"{simpleDiag?.Gen2Collections?.ToString() ?? ""}," +
                    $"{ReportFormat.SchemaVersion},{ReportFormat.MeasurementEpoch}," +
                    $"{detail}," +
                    $"{profile}," +
                    $"{table.RuntimeProfileName},\"{table.RuntimeKnobs}\"," +
                    $"{(row.Result.ThreadControlEnabled ? "true" : "false")}," +
                    $"{(row.Result.InterferenceFilterEnabled ? "true" : "false")}," +
                    $"\"{safeIsolation}\"");
            }
            else
            {
                var diag = row.Result.Diagnostics;
                var diagCols = $"{diag?.Gen0Collections?.ToString() ?? ""},{diag?.Gen1Collections?.ToString() ?? ""},{diag?.Gen2Collections?.ToString() ?? ""}";

                var fullData = $"{commonData},{row.Result.MeanNs:F1},{row.Result.OperationsPerSecond:F1}{percentileData}," +
                               $"{row.Result.StandardDeviationNs:F1}," +
                               $"{row.Result.StandardErrorNs:F1}," +
                               $"{row.Result.MarginOfErrorNs:F1}," +
                               $"{row.Result.ConfidenceIntervalLowerNs:F1}," +
                               $"{row.Result.ConfidenceIntervalUpperNs:F1}," +
                               $"{table.ConfidenceLevel:F2}," +
                               $"{row.Result.CoefficientOfVariation:F4}," +
                               $"{(double.IsNaN(row.Ratio) ? "null" : $"{row.Ratio:F2}")}," +

                               // Empty rather than null when the run had a single launch: there is no
                               // interval to report, which is different from one that could not be
                               // computed. A trend consumer reading a blank knows not to plot it.
                               $"{row.RatioEstimate?.Lower.ToString("F3") ?? ""}," +
                               $"{row.RatioEstimate?.Upper.ToString("F3") ?? ""}," +
                               $"{row.RatioEstimate?.Replicates.ToString() ?? ""}," +
                               $"\"{safeSig}\"," +
                               $"\"{safeEffectMetric}\"," +
                               $"{row.Result.Effect?.Value?.ToString("F4") ?? ""}," +
                               $"\"{safeMagnitude}\"," +
                               $"{row.Result.AllocatedBytesMean?.ToString() ?? "null"}," +
                               $"{diagCols}," +
                               $"{row.Result.MarginOfErrorPercent:F2}," +
                               $"{row.Result.OutliersRemoved}," +
                               $"{ReportFormat.SchemaVersion},{ReportFormat.MeasurementEpoch}," +
                               $"{detail}," +
                               $"{profile}," +
                               $"{table.RuntimeProfileName},\"{table.RuntimeKnobs}\"," +
                               $"{(row.Result.ThreadControlEnabled ? "true" : "false")}," +
                               $"{(row.Result.InterferenceFilterEnabled ? "true" : "false")}";

                if (Detail == ReportDetail.Standard)
                    sb.AppendLine($"{fullData},\"{safeIsolation}\"");
                else
                {
                    var lowerFence = row.Result.LowerFenceNs?.ToString("F1") ?? "";
                    var upperFence = row.Result.UpperFenceNs?.ToString("F1") ?? "";
                    var allocatedBytesMedian = row.Result.AllocatedBytesMedian?.ToString() ?? "";
                    var allocatedBytesP95 = row.Result.AllocatedBytesP95?.ToString() ?? "";
                    var allocatedBytesMax = row.Result.AllocatedBytesMax?.ToString() ?? "";

                    var autoTune = row.Result.AutoTune;
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

                    var safeCategories = string.Join("; ", row.Result.Categories).Replace("\"", "\"\"");
                    var advancedDiag = row.Result.Diagnostics;
                    var heapCommitted = advancedDiag?.HeapCommittedBytes?.ToString() ?? "";
                    var heapFragmented = advancedDiag?.HeapFragmentedBytes?.ToString() ?? "";
                    var excPerOp = advancedDiag?.ExceptionCountPerOp?.ToString("F4") ?? "";
                    var cpuNs = advancedDiag?.CpuTimeNsPerOp?.ToString("F1") ?? "";
                    var cpuRatio = advancedDiag?.CpuWallRatio?.ToString("F4") ?? "";
                    var diagMode = (advancedDiag?.Collected.ToMode().ToString() ?? "").Replace("\"", "\"\"");

                    sb.AppendLine(
                        $"{fullData}," +
                        $"{row.Result.Q1Ns:F1}," +
                        $"{row.Result.Q3Ns:F1}," +
                        $"{row.Result.InterquartileRangeNs:F1}," +
                        $"\"{lowerFence}\"," +
                        $"\"{upperFence}\"," +
                        $"{row.Result.RangeNs:F1}," +
                        $"{row.Result.SampleCount}," +
                        $"{row.Result.Skewness:F4}," +
                        $"{row.Result.Kurtosis:F4}," +
                        $"{row.Result.MedianAbsoluteDeviationNs:F1}," +
                        $"{allocatedBytesMedian}," +
                        $"{allocatedBytesP95}," +
                        $"{allocatedBytesMax}," +
                        $"{row.Result.StandardErrorPercent:F2}," +
                        $"{row.Result.CoefficientOfVariationPercent:F2}," +
                        $"{table.WarmupSamples}," +
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
