using System.Diagnostics;

namespace NBenchmark;

/// <summary>
///     A lightweight console progress reporter that works without Spectre.Console.
///     Uses ANSI escape sequences to render inline progress bars with ETA.
///     This is the default progress when no explicit progress is set and the
///     output is a terminal.
/// </summary>
public sealed class DefaultConsoleProgress : IBenchmarkProgress
{
    private readonly Stopwatch _benchmarkStopwatch = new();
    private readonly Stopwatch _suiteStopwatch = new();
    private int _completedBenchmarks;
    private int _currentIndex;
    private int _currentSample;
    private string _currentName = "";
    private int _currentTotalSamples;
    private bool _inWarmup;
    private int _pulse;
    private int _suiteTotal;

    public Task OnSuiteStartingAsync(
        IReadOnlyList<string> benchmarkNames, int total, CancellationToken cancellationToken)
    {
        _suiteTotal = total;
        _completedBenchmarks = 0;
        _suiteStopwatch.Restart();
        Console.WriteLine($"  Running {total} benchmark(s)...");
        Console.WriteLine();
        return Task.CompletedTask;
    }

    public Task OnWarmupStartingAsync(string name, int totalWarmupSamples, CancellationToken cancellationToken)
    {
        _inWarmup = true;
        _currentSample = 0;
        _currentTotalSamples = totalWarmupSamples;
        RenderStatus();
        return Task.CompletedTask;
    }

    public Task OnWarmupCompletedAsync(string name, CancellationToken cancellationToken)
    {
        _inWarmup = false;
        return Task.CompletedTask;
    }

    public Task OnBenchmarkStartingAsync(string name, int index, int total, CancellationToken cancellationToken)
    {
        _currentName = name;
        _currentIndex = index;
        _suiteTotal = total;
        _currentSample = 0;
        _pulse = 0;
        _benchmarkStopwatch.Restart();
        return Task.CompletedTask;
    }

    public Task OnSampleCompletedAsync(
        string name, int sample, int totalSamples, CancellationToken cancellationToken)
    {
        _currentSample = sample;
        _currentTotalSamples = totalSamples;
        RenderStatus();
        return Task.CompletedTask;
    }

    public Task OnBenchmarkCompletedAsync(BenchmarkResult result, CancellationToken cancellationToken)
    {
        _completedBenchmarks++;
        _benchmarkStopwatch.Stop();

        // Clear status line and print result
        Console.Write("\r\x1b[2K");
        var icon = result.Errored ? "✗" : "✓";

        var timing = result.Errored
            ? result.ErrorMessage
            : BenchmarkFormatter.FormatNs(result.MedianNs);

        var diagSuffix = "";

        if (result.Diagnostics is { } diag && diag.Gen0Collections.HasValue)
        {
            var gen0 = diag.Gen0Collections.Value;
            var gen1 = diag.Gen1Collections ?? 0;
            var gen2 = diag.Gen2Collections ?? 0;
            diagSuffix = $" · {gen0}/{gen1}/{gen2} GC";
        }

        Console.WriteLine($"  {icon} {result.Name}  {timing}{diagSuffix}  ({_benchmarkStopwatch.Elapsed.TotalSeconds:F1}s)");

        return Task.CompletedTask;
    }

    public Task OnSuiteCompletedAsync(
        IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken)
    {
        _suiteStopwatch.Stop();
        Console.WriteLine();
        return Task.CompletedTask;
    }

    private void RenderStatus()
    {
        var phase = _inWarmup ? "warmup" : "measuring";

        if (_currentTotalSamples <= 0)
        {
            // An auto-resolved count has no honest denominator, so show a moving indicator and the
            // live sample count instead of a fake percentage and ETA.
            var indeterminate = IndeterminateBar(20);
            var count = _currentSample > 0 ? $" ({_currentSample} samples)" : "";

            Console.Write(
                $"\r\x1b[2K  [{_currentIndex}/{_suiteTotal}] {_currentName}  {indeterminate} {phase}{count}");

            return;
        }

        var pct = _currentTotalSamples > 0
            ? (int)Math.Round(100.0 * _currentSample / _currentTotalSamples)
            : 0;

        const int barWidth = 20;
        var filled = (int)Math.Round(barWidth * _currentSample / (double)Math.Max(1, _currentTotalSamples));
        filled = Math.Clamp(filled, 0, barWidth);
        var empty = barWidth - filled;
        var bar = $"{new string('\u2588', filled)}{new string('\u2591', empty)}";

        var eta = ComputeEta();
        var etaText = eta.HasValue ? $" ETA {FormatTimeSpan(eta.Value)}" : "";

        Console.Write(
            $"\r\x1b[2K  [{_currentIndex}/{_suiteTotal}] {_currentName}  {bar} {pct}% {phase} ({_currentSample}/{_currentTotalSamples}){etaText}");
    }

    private string IndeterminateBar(int width)
    {
        // A short lit segment that bounces across the track to signal ongoing work.
        const int segment = 3;
        var span = Math.Max(1, width - segment);
        var period = span * 2;
        var pos = _pulse++ % period;

        if (pos > span)
            pos = period - pos;

        var chars = new char[width];

        for (var i = 0; i < width; i++)
        {
            chars[i] = i >= pos && i < pos + segment ? '\u2588' : '\u2591';
        }

        return new string(chars);
    }

    private TimeSpan? ComputeEta()
    {
        if (_currentSample <= 0 || _currentTotalSamples <= 0)
            return null;

        var elapsed = _benchmarkStopwatch.Elapsed;
        var remaining = elapsed / _currentSample * (_currentTotalSamples - _currentSample);

        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalSeconds < 1)
            return "<1s";

        if (ts.TotalSeconds < 60)
            return $"{ts.TotalSeconds:F0}s";

        return $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s";
    }
}
