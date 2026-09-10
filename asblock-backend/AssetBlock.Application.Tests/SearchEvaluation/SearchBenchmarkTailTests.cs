using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.SearchEvaluation.Benchmark;
using AssetBlock.SearchEvaluation.Infrastructure;
using AssetBlock.SearchEvaluation.Profiling;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.SearchEvaluation;

public sealed class SearchBenchmarkTailTests
{
    [Fact]
    public void ValidateTailArguments_WhenCorpusSizeNot50000_ThrowsArgumentException()
    {
        // Arrange
        int[] sizes = [1000, 10000];

        // Act
        Action act = () => SearchBenchmarkTailRunner.ValidateTailArguments(sizes, warmupCount: 10, sampleCount: 100, concurrencyLevels: [1]);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*50000*");
    }

    [Theory]
    [InlineData(new[] { 1, 10 })]
    [InlineData(new[] { 10 })]
    [InlineData(new[] { 5 })]
    public void ValidateTailArguments_WhenConcurrencyNotStrictly1_ThrowsArgumentException(int[] concurrency)
    {
        // Arrange
        int[] sizes = [50000];

        // Act
        Action act = () => SearchBenchmarkTailRunner.ValidateTailArguments(sizes, warmupCount: 10, sampleCount: 100, concurrencyLevels: concurrency);

        // Assert - ensures no silent argument ignoring when multiple or unsupported concurrency levels are specified
        act.Should().Throw<ArgumentException>()
            .WithMessage("*only supports concurrency level [1]*");
    }

    [Fact]
    public void ValidateTailArguments_WhenWarmupNegative_ThrowsArgumentException()
    {
        // Arrange
        int[] sizes = [50000];

        // Act
        Action act = () => SearchBenchmarkTailRunner.ValidateTailArguments(sizes, warmupCount: -1, sampleCount: 100, concurrencyLevels: [1]);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*non-negative*");
    }

    [Fact]
    public void ValidateTailArguments_WhenSamplesNonPositive_ThrowsArgumentException()
    {
        // Arrange
        int[] sizes = [50000];

        // Act
        Action act = () => SearchBenchmarkTailRunner.ValidateTailArguments(sizes, warmupCount: 10, sampleCount: 0, concurrencyLevels: [1]);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*positive*");
    }

    [Fact]
    public void ValidateTailArguments_WhenValid_DoesNotThrow()
    {
        // Arrange
        int[] sizes = [50000];
        int[] concurrency = [1];

        // Act
        Action act = () => SearchBenchmarkTailRunner.ValidateTailArguments(sizes, warmupCount: 100, sampleCount: 1000, concurrencyLevels: concurrency);

        // Assert
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(1, 0.50, 0)]    // N=1, p50 -> 1st element (index 0)
    [InlineData(1, 0.95, 0)]    // N=1, p95 -> 1st element (index 0)
    [InlineData(20, 0.10, 1)]   // N=20, p10 -> ceil(2.0)-1 = index 1
    [InlineData(20, 0.50, 9)]   // N=20, p50 -> ceil(10.0)-1 = index 9 (10th element)
    [InlineData(20, 0.95, 18)]  // N=20, p95 -> ceil(19.0)-1 = index 18 (19th element)
    [InlineData(1000, 0.10, 99)] // N=1000, p10 -> ceil(100.0)-1 = index 99 (100th element)
    [InlineData(1000, 0.50, 499)] // N=1000, p50 -> ceil(500.0)-1 = index 499 (500th element)
    [InlineData(1000, 0.95, 949)] // N=1000, p95 -> ceil(950.0)-1 = index 949 (950th element, not 951st!)
    public void CalculateNearestRankIndex_ReturnsCanonicalNearestRank_OnKnownArrays(int count, double percentile, int expectedIndex)
    {
        // Act
        var actualIndex = SearchBenchmarkTailRunner.CalculateNearestRankIndex(count, percentile);

        // Assert
        actualIndex.Should().Be(expectedIndex);
    }

    [Theory]
    [InlineData(0, 0.50)]
    [InlineData(-5, 0.50)]
    public void CalculateNearestRankIndex_WhenCountInvalid_ThrowsArgumentOutOfRangeException(int count, double percentile)
    {
        Action act = () => SearchBenchmarkTailRunner.CalculateNearestRankIndex(count, percentile);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(100, 0.0)]
    [InlineData(100, -0.1)]
    [InlineData(100, 1.1)]
    public void CalculateNearestRankIndex_WhenPercentileOutOfRange_ThrowsArgumentOutOfRangeException(int count, double percentile)
    {
        Action act = () => SearchBenchmarkTailRunner.CalculateNearestRankIndex(count, percentile);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GenerateBenchmarkQueries_ProducesDeterministicQueriesWithOrdinals()
    {
        // Arrange & Act
        List<(string Text, float[] Vector)> queries1 = SearchBenchmarkRunner.GenerateBenchmarkQueries(100, 768);
        List<(string Text, float[] Vector)> queries2 = SearchBenchmarkRunner.GenerateBenchmarkQueries(100, 768);

        // Assert: deterministic query generation matching SearchBenchmarkRunner exactly
        queries1.Count.Should().Be(100);
        queries2.Count.Should().Be(100);
        for (var i = 0; i < 100; i++)
        {
            queries1[i].Text.Should().Be(queries2[i].Text);
            queries1[i].Vector.Length.Should().Be(768);
            queries1[i].Vector[0].Should().Be(queries2[i].Vector[0]);
        }
    }

    [Fact]
    public void WriteTailReport_EmitsRedactedJsonAndMarkdownWithoutSensitiveParameters()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"tail-report-test-{Guid.NewGuid():N}");
        try
        {
            var provenance = new SystemEnvironmentProvenance(
                OperatingSystem: "Windows",
                Architecture: "X64",
                CpuCores: 8,
                TotalRamBytes: 32_000_000_000,
                GitCommit: "45df74412f8cc195d1da43acabcf2ed86f50da74",
                PostgresVersion: "PostgreSQL 16",
                PgVectorVersion: "0.8.6",
                ContainerImageDigest: "sha256:abc123",
                HnswState: "absent",
                SourceFingerprint: "45df744-test");

            var options = new EmbeddingOptions
            {
                Provider = "Ollama",
                Model = "embeddinggemma:300m-qat-q8_0",
                Revision = "manifest-e84a",
                Digest = "sha256:e84a",
                Dimension = 768,
                ContentSchemaVersion = "asset-public-metadata-v1"
            };

            var planNode = new SanitizedPlanNode(
                NodeType: "Bitmap Heap Scan",
                RelationName: "assets",
                IndexName: "ix_assets_search",
                ParentRelationship: null,
                JoinType: null,
                SortMethod: null,
                SortSpaceType: null,
                SortSpaceUsedKb: null,
                PeakMemoryUsageKb: null,
                ParallelAware: false,
                AsyncCapable: false,
                StartupCost: 1.0,
                TotalCost: 10.0,
                PlanRows: 20,
                PlanWidth: 32,
                ActualStartupTimeMs: 0.5,
                ActualTotalTimeMs: 2.5,
                ActualRows: 20,
                ActualLoops: 1,
                SharedHitBlocks: 15,
                SharedReadBlocks: 0,
                SharedDirtiedBlocks: 0,
                SharedWrittenBlocks: 0,
                TempReadBlocks: 0,
                TempWrittenBlocks: 0,
                ChildPlans: []);

            var queryProfile = new QueryProfileResult(
                Role: "Lexical Fallback Count",
                DatabaseExecutionTimeMs: 2.5,
                DatabasePlanningTimeMs: 0.8,
                ResultRows: 1,
                TotalPlanNodes: 1,
                SharedHitBlocks: 15,
                SharedReadBlocks: 0,
                TempBlocks: 0,
                HasSortSpill: false,
                SortMethod: null,
                SortSpaceUsedKb: null,
                PrimaryNodeSummary: "Bitmap Heap Scan on assets [Rows=20, Loops=1, Time=2.50ms]",
                Plan: planNode);

            var diag = new TailQueryDiagnostic(
                Ordinal: 42,
                Category: "Slowest Tail",
                LatencyMs: 450.2,
                TotalCount: 15000,
                ExplainedQueries: [queryProfile]);

            var reportData = new BenchmarkTailReportData(
                Provenance: provenance,
                PinnedModel: options,
                CorpusSize: 50000,
                WarmupCount: 100,
                SampleCount: 1000,
                Concurrency: 1,
                LexicalP50Ms: 34.2,
                LexicalP95Ms: 750.0,
                LexicalMaxMs: 1100.0,
                SelectedDiagnostics: [diag],
                AttributionSummary: SearchBenchmarkTailRunner.BuildTailAttributionSummary(1000, 34.2, 750.0, 1100.0));

            // Act
            (var jsonPath, var mdPath) = SearchBenchmarkTailRunner.WriteTailReport(reportData, tempDir);

            // Assert
            File.Exists(jsonPath).Should().BeTrue();
            File.Exists(mdPath).Should().BeTrue();

            var mdContent = File.ReadAllText(mdPath);
            mdContent.Should().Contain("AssetBlock Benchmark-Tail Exploratory Diagnostic Evidence");
            mdContent.Should().Contain("Ordinal 42 (Slowest Tail)");
            mdContent.Should().Contain("450.20 ms");
            mdContent.Should().Contain("## Measured Per-Role EXPLAIN Timings");
            mdContent.Should().Contain("Causal bottleneck attribution: unknown");
            mdContent.Should().NotContain("SELECT ");
            mdContent.Should().NotContain("password");
            mdContent.Should().NotContain("secret");
            mdContent.Should().NotContain("PERFORMANCE_TARGETS_MET");
            mdContent.Should().NotContain("Rollout Verdict"); // Diagnostic mode must not have rollout verdict
            mdContent.Should().NotContain("primarily");
            mdContent.Should().NotContain("multi-branch");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void BuildTailAttributionSummary_WhenEmittingDiagnostics_DoesNotClaimCausalBottleneck()
    {
        var summary = SearchBenchmarkTailRunner.BuildTailAttributionSummary(
            sampleCount: 1000,
            p50Ms: 33.6,
            p95Ms: 418.5,
            maxMs: 726.7);

        summary.Should().Contain("Causal bottleneck attribution: unknown");
        summary.Should().Contain("p50=33.6ms");
        summary.Should().Contain("p95=418.5ms");
        summary.Should().NotContain("primarily");
        summary.Should().NotContain("multi-branch");
        summary.Should().NotContain("totalCount aggregation");
        summary.Should().NotContain("ANN");
        summary.Should().NotContain("HNSW");
    }

    [Fact]
    public void WriteTailReport_WhenAttributionProvided_PreservesMeasuredOnlyLanguage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"tail-report-attr-{Guid.NewGuid():N}");
        try
        {
            var provenance = new SystemEnvironmentProvenance(
                OperatingSystem: "Windows",
                Architecture: "X64",
                CpuCores: 8,
                TotalRamBytes: 32_000_000_000,
                GitCommit: "45df74412f8cc195d1da43acabcf2ed86f50da74",
                PostgresVersion: "PostgreSQL 16",
                PgVectorVersion: "0.8.6",
                ContainerImageDigest: "sha256:abc123",
                HnswState: "absent",
                SourceFingerprint: "45df744-test");

            var options = new EmbeddingOptions
            {
                Provider = "Ollama",
                Model = "embeddinggemma:300m-qat-q8_0",
                Revision = "manifest-e84a",
                Digest = "sha256:e84a",
                Dimension = 768,
                ContentSchemaVersion = "asset-public-metadata-v1"
            };

            var attribution = SearchBenchmarkTailRunner.BuildTailAttributionSummary(1000, 34.2, 750.0, 1100.0);
            var reportData = new BenchmarkTailReportData(
                Provenance: provenance,
                PinnedModel: options,
                CorpusSize: 50000,
                WarmupCount: 100,
                SampleCount: 1000,
                Concurrency: 1,
                LexicalP50Ms: 34.2,
                LexicalP95Ms: 750.0,
                LexicalMaxMs: 1100.0,
                SelectedDiagnostics: [],
                AttributionSummary: attribution);

            (_, var mdPath) = SearchBenchmarkTailRunner.WriteTailReport(reportData, tempDir);
            var mdContent = File.ReadAllText(mdPath);

            mdContent.Should().Contain("## Measured Per-Role EXPLAIN Timings");
            mdContent.Should().Contain("Causal bottleneck attribution: unknown");
            mdContent.Should().NotContain("## Bottleneck Attribution");
            mdContent.Should().NotContain("primarily during multi-branch");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
