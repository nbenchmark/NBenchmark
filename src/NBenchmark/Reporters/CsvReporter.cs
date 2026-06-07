using System.Text;

namespace NBenchmark.Reporters;

public sealed class CsvReporter : IReporter
{
    private readonly string _outputPath;

    public CsvReporter(string outputPath = "benchmark-results.csv")
    {
        _outputPath = PathValidation.ValidateOutputPath(outputPath);
    }

    public string Name => "csv";

    public async Task ReportAsync(
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            "Name,Median,Mean,StdDev,StdErr,MarginOfError,CiLower,CiUpper,ConfidenceLevel,CoefficientOfVariation,P95,P99,Ratio,Significant,AllocPerOp");

        var table = BenchmarkTable.Build(results);

        foreach (var row in table.Rows)
        {
            var sig = row.SignificanceLabel switch
            {
                "✓" => "true",
                "~" => "false",
                _ => "",
            };

            var safeName = row.Name.Replace("\"", "\"\"");
            var safeSig = sig.Replace("\"", "\"\"");

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
                $"{row.MeanAllocatedBytes?.ToString() ?? "null"}"
            );
        }

        await File.WriteAllTextAsync(_outputPath, sb.ToString(), cancellationToken);
    }
}