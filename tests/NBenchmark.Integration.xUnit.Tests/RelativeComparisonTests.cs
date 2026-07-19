using NBenchmark.Integration.Abstractions;
using Xunit;

namespace NBenchmark.Integration.xUnit.Tests;

public sealed class RelativeComparisonTests
{
    [Fact]
    public void Check_Passes_When_Candidate_Faster_Than_Reference()
    {
        var candidate = CreateResult("Candidate", 100);
        var reference = CreateResult("Reference", 200);
        var candidateSamples = GenerateSamples(100, 50);
        var referenceSamples = GenerateSamples(200, 50);

        var violations = RelativeComparison.Check(
            candidate, candidateSamples, reference, referenceSamples, 1.2);

        Assert.Empty(violations);
    }

    [Fact]
    public void Check_Fails_When_Candidate_Significantly_Slower()
    {
        var candidate = CreateResult("Candidate", 600);
        var reference = CreateResult("Reference", 200);
        var candidateSamples = GenerateSamples(600, 100);
        var referenceSamples = GenerateSamples(200, 100);

        var violations = RelativeComparison.Check(
            candidate, candidateSamples, reference, referenceSamples, 1.2);

        Assert.Single(violations);
        Assert.Contains("Regression detected", violations[0]);
        Assert.Contains("3.00x", violations[0]);
    }

    [Fact]
    public void Check_Passes_When_Slowdown_Not_Significant()
    {
        var candidate = CreateResult("Candidate", 600);
        var reference = CreateResult("Reference", 200);
        var referenceSamples = GenerateSamples(200, 100);
        var candidateSamples = GenerateSamples(200, 100);

        var violations = RelativeComparison.Check(
            candidate, candidateSamples, reference, referenceSamples, 1.2);

        Assert.Empty(violations);
    }

    [Fact]
    public void Check_Passes_When_Significant_But_Ratio_Within_Gate()
    {
        var candidate = CreateResult("Candidate", 220);
        var reference = CreateResult("Reference", 200);
        var candidateSamples = GenerateSamples(220, 100);
        var referenceSamples = GenerateSamples(200, 100);

        var violations = RelativeComparison.Check(
            candidate, candidateSamples, reference, referenceSamples, 1.2);

        Assert.Empty(violations);
    }

    [Fact]
    public void Check_Handles_NaN_PValue_With_Insufficient_Samples()
    {
        var candidate = CreateResult("Candidate", 600);
        var reference = CreateResult("Reference", 200);
        var candidateSamples = new double[] { 600 };
        var referenceSamples = new double[] { 200 };

        var violations = RelativeComparison.Check(
            candidate, candidateSamples, reference, referenceSamples, 1.2);

        Assert.Empty(violations);
    }

    [Fact]
    public void Check_Returns_Violation_When_Candidate_Samples_Empty()
    {
        var candidate = CreateResult("Candidate", 600);
        var reference = CreateResult("Reference", 200);
        var referenceSamples = GenerateSamples(200, 50);

        var violations = RelativeComparison.Check(
            candidate, [], reference, referenceSamples, 1.2);

        Assert.Single(violations);
        Assert.Contains("no raw samples", violations[0]);
    }

    [Fact]
    public void Check_Returns_Violation_When_Reference_Samples_Empty()
    {
        var candidate = CreateResult("Candidate", 600);
        var reference = CreateResult("Reference", 200);
        var candidateSamples = GenerateSamples(600, 50);

        var violations = RelativeComparison.Check(
            candidate, candidateSamples, reference, [], 1.2);

        Assert.Single(violations);
        Assert.Contains("no raw samples", violations[0]);
    }

    [Fact]
    public void Check_Handles_Non_Positive_Reference_Mean()
    {
        var candidate = CreateResult("Candidate", 500);
        var reference = CreateResult("Reference", 0);
        var candidateSamples = GenerateSamples(500, 50);
        var referenceSamples = GenerateSamples(0, 50);

        var violations = RelativeComparison.Check(
            candidate, candidateSamples, reference, referenceSamples, 1.2);

        Assert.Single(violations);
        Assert.Contains("non-positive reference", violations[0]);
    }

    [Fact]
    public void CheckStructured_Regression_PopulatesRatioPValueCliffsDelta()
    {
        var candidate = CreateResult("Candidate", 600);
        var reference = CreateResult("Reference", 200);
        var candidateSamples = GenerateSamples(600, 100);
        var referenceSamples = GenerateSamples(200, 100);

        var verdict = RelativeComparison.CheckStructured(
            candidate, candidateSamples, reference, referenceSamples, 1.2);

        Assert.True(verdict.IsRegression);
        Assert.Single(verdict.Violations);
        Assert.Equal(3.0, verdict.Ratio, 12);
        Assert.False(double.IsNaN(verdict.PValue));
        Assert.True(verdict.PValue < 0.05);
        Assert.False(double.IsNaN(verdict.CliffsDelta));
    }

    [Fact]
    public void CheckStructured_CandidateFasterThanReference_IsRegressionFalse_RatioBelowOne()
    {
        var candidate = CreateResult("Candidate", 100);
        var reference = CreateResult("Reference", 200);
        var candidateSamples = GenerateSamples(100, 50);
        var referenceSamples = GenerateSamples(200, 50);

        var verdict = RelativeComparison.CheckStructured(
            candidate, candidateSamples, reference, referenceSamples, 1.2);

        Assert.False(verdict.IsRegression);
        Assert.Empty(verdict.Violations);
        Assert.Equal(0.5, verdict.Ratio, 12);
    }

    [Fact]
    public void CheckStructured_SignificantButWithinRatioGate_IsRegressionFalse_StillReturnsPValue()
    {
        // Distinct samples so the Mann-Whitney test runs and returns a p-value,
        // but the ratio is below the gate so IsRegression must be false. The
        // structured fields (Ratio, PValue, CliffsDelta) are populated even on
        // the non-regression path.
        var candidate = CreateResult("Candidate", 220);
        var reference = CreateResult("Reference", 200);
        var candidateSamples = GenerateSamples(220, 100);
        var referenceSamples = GenerateSamples(200, 100);

        var verdict = RelativeComparison.CheckStructured(
            candidate, candidateSamples, reference, referenceSamples, 1.2);

        Assert.False(verdict.IsRegression);
        Assert.Empty(verdict.Violations);
        Assert.Equal(1.1, verdict.Ratio, 12);
        Assert.False(double.IsNaN(verdict.PValue));
        Assert.False(double.IsNaN(verdict.CliffsDelta));
    }

    [Fact]
    public void CheckStructured_ErroredCandidate_ReturnsNaNFields_IsRegressionFalse()
    {
        var candidate = CreateResult("Candidate", 600);
        candidate = candidate with { Errored = true, ErrorMessage = "boom" };
        var reference = CreateResult("Reference", 200);
        var candidateSamples = GenerateSamples(600, 50);
        var referenceSamples = GenerateSamples(200, 50);

        var verdict = RelativeComparison.CheckStructured(
            candidate, candidateSamples, reference, referenceSamples, 1.2);

        Assert.False(verdict.IsRegression);
        Assert.Empty(verdict.Violations);
        Assert.True(double.IsNaN(verdict.Ratio));
        Assert.True(double.IsNaN(verdict.PValue));
        Assert.True(double.IsNaN(verdict.CliffsDelta));
    }

    [Fact]
    public void CheckStructured_ErroredReference_ReturnsViolationAndNaNFields()
    {
        var candidate = CreateResult("Candidate", 600);
        var reference = CreateResult("Reference", 200);
        reference = reference with { Errored = true, ErrorMessage = "ref-boom" };
        var candidateSamples = GenerateSamples(600, 50);
        var referenceSamples = GenerateSamples(200, 50);

        var verdict = RelativeComparison.CheckStructured(
            candidate, candidateSamples, reference, referenceSamples, 1.2);

        Assert.False(verdict.IsRegression);
        Assert.Single(verdict.Violations);
        Assert.Contains("errored", verdict.Violations[0]);
        Assert.True(double.IsNaN(verdict.Ratio));
        Assert.True(double.IsNaN(verdict.PValue));
        Assert.True(double.IsNaN(verdict.CliffsDelta));
    }

    [Fact]
    public void CheckStructured_EmptyCandidateSamples_ReturnsViolationAndNaNFields()
    {
        var candidate = CreateResult("Candidate", 600);
        var reference = CreateResult("Reference", 200);
        var referenceSamples = GenerateSamples(200, 50);

        var verdict = RelativeComparison.CheckStructured(
            candidate, [], reference, referenceSamples, 1.2);

        Assert.False(verdict.IsRegression);
        Assert.Single(verdict.Violations);
        Assert.Contains("no raw samples", verdict.Violations[0]);
        Assert.True(double.IsNaN(verdict.Ratio));
        Assert.True(double.IsNaN(verdict.PValue));
        Assert.True(double.IsNaN(verdict.CliffsDelta));
    }

    [Fact]
    public void CheckStructured_NonPositiveReferenceMean_RegressionWhenCandidatePositive_RatioNaN()
    {
        var candidate = CreateResult("Candidate", 500);
        var reference = CreateResult("Reference", 0);
        var candidateSamples = GenerateSamples(500, 50);
        var referenceSamples = GenerateSamples(0, 50);

        var verdict = RelativeComparison.CheckStructured(
            candidate, candidateSamples, reference, referenceSamples, 1.2);

        Assert.True(verdict.IsRegression);
        Assert.Single(verdict.Violations);
        Assert.True(double.IsNaN(verdict.Ratio));
        Assert.True(double.IsNaN(verdict.PValue));
        Assert.True(double.IsNaN(verdict.CliffsDelta));
    }

    [Fact]
    public void CheckStructured_NonPositiveReferenceAndCandidate_IsRegressionFalse_NoViolations()
    {
        var candidate = CreateResult("Candidate", 0);
        var reference = CreateResult("Reference", 0);
        var candidateSamples = GenerateSamples(0, 50);
        var referenceSamples = GenerateSamples(0, 50);

        var verdict = RelativeComparison.CheckStructured(
            candidate, candidateSamples, reference, referenceSamples, 1.2);

        Assert.False(verdict.IsRegression);
        Assert.Empty(verdict.Violations);
        Assert.True(double.IsNaN(verdict.Ratio));
    }

    [Fact]
    public void Check_DelegatesToCheckStructured_ViolationsMatch()
    {
        // The string-returning Check must produce the same Violations list as
        // CheckStructured.Violations - it is a thin formatter over the
        // structured verdict.
        var candidate = CreateResult("Candidate", 600);
        var reference = CreateResult("Reference", 200);
        var candidateSamples = GenerateSamples(600, 100);
        var referenceSamples = GenerateSamples(200, 100);

        var structured = RelativeComparison.CheckStructured(
            candidate, candidateSamples, reference, referenceSamples, 1.2);

        var strings = RelativeComparison.Check(
            candidate, candidateSamples, reference, referenceSamples, 1.2);

        Assert.Equal(structured.Violations, strings);
    }

    private static BenchmarkResult CreateResult(string name, double mean)
    {
        return new BenchmarkResult
        {
            Name = name,
            Mean = mean,
            Median = mean * 0.9,
            Percentiles = [],
            Min = mean * 0.5,
            Max = mean * 2,
            StandardDeviation = mean * 0.1,
            MeasuredIterations = 100,
            WarmupIterations = 25,
            Q1 = 0,
            Q3 = 0,
            InterquartileRange = 0,
            OutliersRemoved = 0,
            N = 100,
            Skewness = 0,
            Kurtosis = 0,
            Mad = 0,
            AllocMedian = null,
            AllocP95 = null,
            AllocMax = null,
        };
    }

    private static double[] GenerateSamples(double mean, int count)
    {
        var samples = new double[count];

        for (var i = 0; i < count; i++)
        {
            samples[i] = mean + (i % 10 - 5) * 0.05 * mean;
        }

        return samples;
    }
}
