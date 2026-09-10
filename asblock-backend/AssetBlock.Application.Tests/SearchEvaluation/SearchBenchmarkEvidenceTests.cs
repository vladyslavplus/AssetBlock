using AssetBlock.Application.Common;
using AssetBlock.SearchEvaluation.Benchmark;
using AssetBlock.SearchEvaluation.Reporting;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.SearchEvaluation;

public sealed class SearchBenchmarkEvidenceTests
{
    private static readonly int[] _prescribedSizes = [1000, 10000, 50000];
    private static readonly int[] _prescribedConcurrency = [1, 10];
    private const int PRESCRIBED_WARMUP = 100;
    private const int PRESCRIBED_SAMPLES = 1000;

    [Fact]
    public void EvaluateBenchmarkDecision_WhenConcurrency10OnlyFailsDbHybrid_ShouldFailDecisionWithoutAutomaticHnsw()
    {
        // Arrange: 50k Concurrency 1 passes, but Concurrency 10 DB Hybrid fails (> 200 ms).
        // Aggregate DB Hybrid includes lexical search, vector scan, candidate fusion, and entity hydration.
        // Aggregate failure requires bottleneck attribution via profile-sql and must NOT automatically recommend HNSW.
        List<CorpusBenchmarkResult> results = CreatePrescribedResults(
            c1DbHybridP95: 120.0,
            c1FullPathP95: 180.0,
            c1LexicalP95: 80.0,
            c10DbHybridP95: 250.0, // FAILS threshold <= 200ms
            c10FullPathP95: 280.0,
            c10LexicalP95: 140.0);

        // Act
        (BenchmarkProtocolConformance protocol, BenchmarkEvidenceCompleteness completeness, BenchmarkPerformanceDecision decision, var exactScanPassed, var conclusion) =
            SearchBenchmarkRunner.EvaluateBenchmarkDecision(
                _prescribedSizes,
                PRESCRIBED_WARMUP,
                PRESCRIBED_SAMPLES,
                _prescribedConcurrency,
                skipOllama: false,
                results);

        // Assert
        protocol.IsPrescribed.Should().BeTrue();
        completeness.IsComplete.Should().BeTrue();
        decision.Passed50KTargets.Should().BeFalse();
        decision.HnswRecommended.Should().BeFalse(); // Aggregate failure without branch evidence must NOT recommend HNSW!
        exactScanPassed.Should().BeFalse();
        decision.RolloutVerdict.Should().Contain("PERFORMANCE_TARGETS_FAILED");
        decision.RolloutVerdict.Should().Contain("bottleneck attribution required");
        conclusion.Should().Contain("FAILED");
        conclusion.Should().Contain("Bottleneck attribution via profile-sql is required");
    }

    [Fact]
    public void EvaluateBenchmarkDecision_WhenLexicalOnlyFails_ShouldFailDecisionWithoutHnswRecommendation()
    {
        // Arrange: 50k DB Hybrid passes <= 200ms, Full Path passes <= 300ms, but Lexical fails (> 200 ms)
        List<CorpusBenchmarkResult> results = CreatePrescribedResults(
            c1DbHybridP95: 110.0,
            c1FullPathP95: 190.0,
            c1LexicalP95: 230.0, // FAILS threshold <= 200ms
            c10DbHybridP95: 180.0,
            c10FullPathP95: 260.0,
            c10LexicalP95: 250.0); // FAILS threshold <= 200ms

        // Act
        (BenchmarkProtocolConformance protocol, BenchmarkEvidenceCompleteness completeness, BenchmarkPerformanceDecision decision, var exactScanPassed, var conclusion) =
            SearchBenchmarkRunner.EvaluateBenchmarkDecision(
                _prescribedSizes,
                PRESCRIBED_WARMUP,
                PRESCRIBED_SAMPLES,
                _prescribedConcurrency,
                skipOllama: false,
                results);

        // Assert
        protocol.IsPrescribed.Should().BeTrue();
        completeness.IsComplete.Should().BeTrue();
        decision.Passed50KTargets.Should().BeFalse();
        decision.HnswRecommended.Should().BeFalse(); // Lexical failure must NOT trigger HNSW recommendation!
        exactScanPassed.Should().BeFalse();
    }

    [Fact]
    public void EvaluateBenchmarkDecision_WhenSamplesIncomplete_ShouldMarkIncompleteAndBlockRolloutVerdict()
    {
        // Arrange: run requested 1000 samples, but only 500 were captured in metrics
        List<CorpusBenchmarkResult> results = CreatePrescribedResults(
            c1DbHybridP95: 100.0,
            c1FullPathP95: 150.0,
            c1LexicalP95: 80.0,
            c10DbHybridP95: 120.0,
            c10FullPathP95: 180.0,
            c10LexicalP95: 100.0,
            sampleCount: 500);

        // Act
        (BenchmarkProtocolConformance protocol, BenchmarkEvidenceCompleteness completeness, BenchmarkPerformanceDecision decision, var exactScanPassed, var conclusion) =
            SearchBenchmarkRunner.EvaluateBenchmarkDecision(
                _prescribedSizes,
                PRESCRIBED_WARMUP,
                PRESCRIBED_SAMPLES,
                _prescribedConcurrency,
                skipOllama: false,
                results);

        // Assert
        completeness.IsComplete.Should().BeFalse();
        completeness.MissingOrFailedSamples.Should().BeGreaterThan(0);
        exactScanPassed.Should().BeFalse();
        decision.RolloutVerdict.Should().Contain("BLOCKED");
    }

    [Fact]
    public void EvaluateBenchmarkDecision_WhenSkipOllamaSpecified_ShouldMarkIncompleteAndBlockRolloutVerdict()
    {
        // Arrange: run with skipOllama = true
        List<CorpusBenchmarkResult> results = CreatePrescribedResults(
            c1DbHybridP95: 100.0,
            c1FullPathP95: 150.0,
            c1LexicalP95: 80.0,
            c10DbHybridP95: 120.0,
            c10FullPathP95: 180.0,
            c10LexicalP95: 100.0);

        // Act
        (BenchmarkProtocolConformance protocol, BenchmarkEvidenceCompleteness completeness, BenchmarkPerformanceDecision decision, var exactScanPassed, var conclusion) =
            SearchBenchmarkRunner.EvaluateBenchmarkDecision(
                _prescribedSizes,
                PRESCRIBED_WARMUP,
                PRESCRIBED_SAMPLES,
                _prescribedConcurrency,
                skipOllama: true,
                results);

        // Assert
        completeness.SkipOllama.Should().BeTrue();
        completeness.IsComplete.Should().BeFalse();
        exactScanPassed.Should().BeFalse();
        decision.RolloutVerdict.Should().Contain("BLOCKED");
    }

    [Fact]
    public void EvaluateBenchmarkDecision_WhenHiddenFallbackOccurs_ShouldMarkIncompleteEvidence()
    {
        // Arrange: Tracker recorded lexical fallback during cached vector path
        var tracker = new SearchBenchmarkRunner.BenchmarkExecutionTracker();
        tracker.RecordLexicalInvocation();

        List<CorpusBenchmarkResult> results = CreatePrescribedResults(
            c1DbHybridP95: 100.0,
            c1FullPathP95: 150.0,
            c1LexicalP95: 80.0,
            c10DbHybridP95: 120.0,
            c10FullPathP95: 180.0,
            c10LexicalP95: 100.0);

        // Act
        (BenchmarkProtocolConformance protocol, BenchmarkEvidenceCompleteness completeness, BenchmarkPerformanceDecision decision, var exactScanPassed, var conclusion) =
            SearchBenchmarkRunner.EvaluateBenchmarkDecision(
                _prescribedSizes,
                PRESCRIBED_WARMUP,
                PRESCRIBED_SAMPLES,
                _prescribedConcurrency,
                skipOllama: false,
                results,
                tracker);

        // Assert
        completeness.IsComplete.Should().BeFalse();
        completeness.HiddenLexicalFallbacks.Should().Be(1);
        completeness.IncompletenessReasons.Should().Contain(r => r.Contains("Hidden lexical fallback detected"));
        exactScanPassed.Should().BeFalse();
    }

    [Fact]
    public async Task ObservableResultCache_WhenGetCalled_AlwaysBypassesAndReturnsNull()
    {
        // Arrange
        var tracker = new SearchBenchmarkRunner.BenchmarkExecutionTracker();
        var cache = new SearchBenchmarkRunner.ObservableResultCache(tracker);

        // Act
        var result = await cache.Get<string>("test-key");

        // Assert
        result.Should().BeNull();
        tracker.ResultCacheHits.Should().Be(0);
    }

    [Fact]
    public void GenerateDeterministicVector_WhenGivenDuplicateQueries_ProducesIdenticalVectors()
    {
        // Arrange
        var query1 = "Fantasy Medieval Castle";
        var query2 = "  Fantasy   Medieval   Castle  ";
        var normalized1 = CatalogSearchNormalization.NormalizeSearchQuery(query1);
        var normalized2 = CatalogSearchNormalization.NormalizeSearchQuery(query2);

        // Act
        var vec1 = SearchBenchmarkRunner.GenerateDeterministicVector(normalized1!, 768);
        var vec2 = SearchBenchmarkRunner.GenerateDeterministicVector(normalized2!, 768);

        // Assert
        vec1.Length.Should().Be(768);
        vec2.Length.Should().Be(768);
        vec1.Should().Equal(vec2);
    }

    private static List<CorpusBenchmarkResult> CreatePrescribedResults(
        double c1DbHybridP95,
        double c1FullPathP95,
        double c1LexicalP95,
        double c10DbHybridP95,
        double c10FullPathP95,
        double c10LexicalP95,
        int sampleCount = PRESCRIBED_SAMPLES)
    {
        var list = new List<CorpusBenchmarkResult>();

        foreach (var size in _prescribedSizes)
        {
            var is50k = size == 50000;
            var c1Hybrid = is50k ? c1DbHybridP95 : 30.0;
            var c1Full = is50k ? c1FullPathP95 : 40.0;
            var c1Lex = is50k ? c1LexicalP95 : 20.0;

            var c10Hybrid = is50k ? c10DbHybridP95 : 50.0;
            var c10Full = is50k ? c10FullPathP95 : 70.0;
            var c10Lex = is50k ? c10LexicalP95 : 35.0;

            var c1Paths = new List<PathBenchmarkMetrics>
            {
                new("DB Hybrid Retrieval", sampleCount, c1Hybrid, c1Hybrid * 0.8, c1Hybrid, c1Hybrid * 0.5, c1Hybrid * 1.5, 200.0, c1Hybrid <= 200.0),
                new("Full Catalog (Cached Query Vector)", sampleCount, c1Full, c1Full * 0.8, c1Full, c1Full * 0.5, c1Full * 1.5, 300.0, c1Full <= 300.0),
                new("Lexical Fallback", sampleCount, c1Lex, c1Lex * 0.8, c1Lex, c1Lex * 0.5, c1Lex * 1.5, 200.0, c1Lex <= 200.0),
                new("Uncached Ollama Query Generation", sampleCount, 400.0, 350.0, 500.0, 200.0, 800.0, null, null)
            };

            var c10Paths = new List<PathBenchmarkMetrics>
            {
                new("DB Hybrid Retrieval", sampleCount, c10Hybrid, c10Hybrid * 0.8, c10Hybrid, c10Hybrid * 0.5, c10Hybrid * 1.5, 200.0, c10Hybrid <= 200.0),
                new("Full Catalog (Cached Query Vector)", sampleCount, c10Full, c10Full * 0.8, c10Full, c10Full * 0.5, c10Full * 1.5, 300.0, c10Full <= 300.0),
                new("Lexical Fallback", sampleCount, c10Lex, c10Lex * 0.8, c10Lex, c10Lex * 0.5, c10Lex * 1.5, 200.0, c10Lex <= 200.0),
                new("Uncached Ollama Query Generation", sampleCount, 400.0, 350.0, 500.0, 200.0, 800.0, null, null)
            };

            var concResults = new List<ConcurrencyBenchmarkResult>
            {
                new(1, c1Paths),
                new(10, c10Paths)
            };

            list.Add(new CorpusBenchmarkResult(size, PRESCRIBED_WARMUP, sampleCount, concResults));
        }

        return list;
    }
}
