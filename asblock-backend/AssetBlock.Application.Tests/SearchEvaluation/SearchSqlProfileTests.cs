using Ardalis.Result;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.UseCases.Assets.GetAssets;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.SearchEvaluation.Benchmark;
using AssetBlock.SearchEvaluation.Infrastructure;
using AssetBlock.SearchEvaluation.Profiling;
using AssetBlock.SearchEvaluation.Reporting;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using NSubstitute;

namespace AssetBlock.Application.Tests.SearchEvaluation;

public sealed class SearchSqlProfileTests
{
    private const string CANARY_SECRET_LITERAL = "CANARY_SECRET_SEARCH_TERM_42";
    private const string CANARY_VECTOR_VAL = "0.9876543210123456";

    [Fact]
    public void ExplainPlanSanitizer_WhenExplainContainsCanaryLiteralsAndExpressions_RedactsAllLiterals()
    {
        // Arrange: raw EXPLAIN JSON containing Filter, Index Cond, Sort Key, Output with canary literals and vector values
        var rawExplainJson = $$"""
        [
          {
            "Plan": {
              "Node Type": "Index Scan",
              "Parallel Aware": false,
              "Async Capable": false,
              "Relation Name": "assets",
              "Index Name": "ix_assets_search",
              "Startup Cost": 0.42,
              "Total Cost": 8.44,
              "Plan Rows": 1,
              "Plan Width": 32,
              "Actual Startup Time": 0.05,
              "Actual Total Time": 0.12,
              "Actual Rows": 5.0,
              "Actual Loops": 1.0,
              "Output": ["id", "title", "description", "'{{CANARY_SECRET_LITERAL}}'"],
              "Index Cond": "(title = '{{CANARY_SECRET_LITERAL}}'::text)",
              "Filter": "(vector <-> '[{{CANARY_VECTOR_VAL}}]'::vector < 0.5)",
              "Shared Hit Blocks": 12,
              "Shared Read Blocks": 3,
              "Shared Dirtied Blocks": 0,
              "Shared Written Blocks": 0,
              "Temp Read Blocks": 0,
              "Temp Written Blocks": 0,
              "Plans": [
                {
                  "Node Type": "Sort",
                  "Sort Method": "quicksort",
                  "Sort Space Used": 32,
                  "Sort Space Type": "Memory",
                  "Sort Key": ["price", "'{{CANARY_SECRET_LITERAL}}'"],
                  "Startup Cost": 1.0,
                  "Total Cost": 2.0,
                  "Plan Rows": 5,
                  "Plan Width": 16,
                  "Actual Startup Time": 0.02,
                  "Actual Total Time": 0.04,
                  "Actual Rows": 5.0,
                  "Actual Loops": 1.0,
                  "Shared Hit Blocks": 4,
                  "Shared Read Blocks": 0,
                  "Shared Dirtied Blocks": 0,
                  "Shared Written Blocks": 0,
                  "Temp Read Blocks": 0,
                  "Temp Written Blocks": 0
                }
              ]
            },
            "Planning Time": 0.15,
            "Execution Time": 0.25
          }
        ]
        """;

        // Act
        SanitizedExplainPlan sanitized = ExplainPlanSanitizer.Sanitize(rawExplainJson);

        // Assert - Allowlisted structural and numeric fields preserved
        sanitized.PlanningTimeMs.Should().Be(0.15);
        sanitized.ExecutionTimeMs.Should().Be(0.25);
        sanitized.RootNode.NodeType.Should().Be("Index Scan");
        sanitized.RootNode.RelationName.Should().Be("assets");
        sanitized.RootNode.IndexName.Should().Be("ix_assets_search");
        sanitized.RootNode.ActualTotalTimeMs.Should().Be(0.12);
        sanitized.RootNode.ActualRows.Should().Be(5.0);
        sanitized.RootNode.SharedHitBlocks.Should().Be(12);
        sanitized.RootNode.ChildPlans.Should().HaveCount(1);

        SanitizedPlanNode child = sanitized.RootNode.ChildPlans[0];
        child.NodeType.Should().Be("Sort");
        child.SortMethod.Should().Be("quicksort");
        child.SortSpaceType.Should().Be("Memory");
        child.SortSpaceUsedKb.Should().Be(32);

        // Assert - Serialized representation does NOT contain canary literals or vector values
        var serialized = System.Text.Json.JsonSerializer.Serialize(sanitized);
        serialized.Should().NotContain(CANARY_SECRET_LITERAL);
        serialized.Should().NotContain(CANARY_VECTOR_VAL);
        serialized.Should().NotContain("Filter");
        serialized.Should().NotContain("Index Cond");
        serialized.Should().NotContain("Sort Key");
        serialized.Should().NotContain("Output");
    }

    [Fact]
    public void GenerateSqlProfileMarkdown_WhenGeneratingReport_DoesNotLeakCanariesOrRawSql()
    {
        // Arrange
        var rootNode = new SanitizedPlanNode(
            NodeType: "Seq Scan",
            RelationName: "assets",
            IndexName: null,
            ParentRelationship: null,
            JoinType: null,
            SortMethod: null,
            SortSpaceType: null,
            SortSpaceUsedKb: null,
            PeakMemoryUsageKb: null,
            ParallelAware: false,
            AsyncCapable: false,
            StartupCost: 0.0,
            TotalCost: 1500.0,
            PlanRows: 50000,
            PlanWidth: 32,
            ActualStartupTimeMs: 0.5,
            ActualTotalTimeMs: 45.2,
            ActualRows: 200,
            ActualLoops: 1.0,
            SharedHitBlocks: 850,
            SharedReadBlocks: 50,
            SharedDirtiedBlocks: 0,
            SharedWrittenBlocks: 0,
            TempReadBlocks: 0,
            TempWrittenBlocks: 0,
            ChildPlans: []);

        var query = new QueryProfileResult(
            Role: "Hybrid Lexical Candidates",
            DatabaseExecutionTimeMs: 45.2,
            DatabasePlanningTimeMs: 0.8,
            ResultRows: 200,
            TotalPlanNodes: 1,
            SharedHitBlocks: 850,
            SharedReadBlocks: 50,
            TempBlocks: 0,
            HasSortSpill: false,
            SortMethod: null,
            SortSpaceUsedKb: null,
            PrimaryNodeSummary: "Seq Scan on assets [Rows=200, Loops=1, Time=45.20ms]",
            Plan: rootNode);

        var scenario = new ScenarioProfileResult(
            ScenarioId: "SYNTH-01-UNFILTERED",
            Description: "Unfiltered query search",
            FilterEligibleCount: 50000,
            SemanticEligibleCount: 50000,
            FusedResultCount: 400,
            HybridHandlerLatency: new ScenarioLatencyMetrics(20, 50.0, 48.0, 52.0, 40.0, 60.0),
            HybridStoreLatency: new ScenarioLatencyMetrics(20, 47.0, 46.0, 49.0, 39.0, 55.0),
            LexicalFallbackHandlerLatency: new ScenarioLatencyMetrics(20, 30.0, 29.0, 32.0, 25.0, 35.0),
            LexicalFallbackStoreLatency: new ScenarioLatencyMetrics(20, 28.0, 27.0, 30.0, 24.0, 33.0),
            HybridQueries: [query],
            LexicalFallbackQueries: [],
            DominantBottleneckSummary: "Sequential scan on assets");

        var provenance = new SystemEnvironmentProvenance(
            GitCommit: "abcdef123456",
            OperatingSystem: "Windows 11",
            Architecture: "X64",
            CpuCores: 8,
            TotalRamBytes: 16L * 1024 * 1024 * 1024,
            ContainerImageDigest: "pgvector/pgvector:pg17@sha256:test",
            PostgresVersion: "PostgreSQL 17.2",
            PgVectorVersion: "0.8.0",
            HnswState: "NONE (Sequential scan active)",
            IsGitDirty: false,
            SourceFingerprint: "fingerprint-test");

        var reportData = new SqlProfileReportData(
            provenance,
            new EmbeddingOptions
            {
                Model = "embeddinggemma:300m-qat-q8_0",
                Revision = "test-rev",
                Digest = "sha256:test",
                Dimension = 768
            },
            DiagnosticWarmupCount: 5,
            DiagnosticSampleCount: 20,
            DiagnosticConcurrency: 1,
            Scenarios: [scenario],
            NegativeFixtureCounts: new Dictionary<string, int> { ["Soft-Deleted Assets"] = 20 },
            ActualIndexState: "ix_asset_embeddings_model_key_vector (NONE - Exact Sequential Scan active)",
            MeasurementScope: "Direct IAssetStore.GetPaged and GetAssetsQueryHandler invocations on disposable test DB",
            BottleneckAttributionSummary: "Diagnostic summary");

        // Act
        var markdown = SearchEvaluationReportWriter.GenerateSqlProfileMarkdown(reportData);

        // Assert
        markdown.Should().NotContain(CANARY_SECRET_LITERAL);
        markdown.Should().NotContain(CANARY_VECTOR_VAL);
        markdown.Should().NotContain("SELECT ");
        markdown.Should().NotContain("WHERE ");
        markdown.Should().Contain("ix_asset_embeddings_model_key_vector (NONE - Exact Sequential Scan active)");
        markdown.Should().Contain("SYNTH-01-UNFILTERED");
        markdown.Should().Contain("Seq Scan on assets");
        markdown.Should().Contain("Filter-Eligible: 50,000");
        markdown.Should().Contain("**Diagnostic Repetitions:** Warmup = 5, Samples = 20");
    }

    [Fact]
    public void SanitizedExplainPlan_BufferTotals_ReadsDirectlyFromRootNodeWithoutDoubleCountingChildren()
    {
        // Arrange: Root node with inclusive counters (SharedHit=126064, SharedRead=28697)
        // and child node with internal counters (SharedHit=50000, SharedRead=10000).
        // Statement totals must be taken directly from RootNode, not summed with children.
        var childNode = new SanitizedPlanNode(
            NodeType: "Seq Scan",
            RelationName: "asset_embeddings",
            IndexName: null,
            ParentRelationship: "Outer",
            JoinType: null,
            SortMethod: null,
            SortSpaceType: null,
            SortSpaceUsedKb: null,
            PeakMemoryUsageKb: null,
            ParallelAware: false,
            AsyncCapable: false,
            StartupCost: 0.0,
            TotalCost: 500.0,
            PlanRows: 50000,
            PlanWidth: 768,
            ActualStartupTimeMs: 0.1,
            ActualTotalTimeMs: 120.5,
            ActualRows: 50000,
            ActualLoops: 1.0,
            SharedHitBlocks: 50000,
            SharedReadBlocks: 10000,
            SharedDirtiedBlocks: 0,
            SharedWrittenBlocks: 0,
            TempReadBlocks: 0,
            TempWrittenBlocks: 0,
            ChildPlans: []);

        var rootNode = new SanitizedPlanNode(
            NodeType: "Gather Merge",
            RelationName: null,
            IndexName: null,
            ParentRelationship: null,
            JoinType: null,
            SortMethod: "quicksort",
            SortSpaceType: "Memory",
            SortSpaceUsedKb: 64,
            PeakMemoryUsageKb: null,
            ParallelAware: true,
            AsyncCapable: false,
            StartupCost: 100.0,
            TotalCost: 1200.0,
            PlanRows: 201,
            PlanWidth: 768,
            ActualStartupTimeMs: 5.0,
            ActualTotalTimeMs: 150.2,
            ActualRows: 201,
            ActualLoops: 1.0,
            SharedHitBlocks: 126064,
            SharedReadBlocks: 28697,
            SharedDirtiedBlocks: 0,
            SharedWrittenBlocks: 0,
            TempReadBlocks: 0,
            TempWrittenBlocks: 0,
            ChildPlans: [childNode]);

        var plan = new SanitizedExplainPlan(
            PlanningTimeMs: 1.2,
            ExecutionTimeMs: 150.2,
            RootNode: rootNode);

        // Act & Assert
        plan.TotalSharedHitBlocks.Should().Be(126064); // NOT 126064 + 50000 = 176064
        plan.TotalSharedReadBlocks.Should().Be(28697); // NOT 28697 + 10000 = 38697
        plan.TotalTempBlocks.Should().Be(0);
        plan.HasSortSpill.Should().BeFalse();
        plan.TotalNodesCount.Should().Be(2);
    }

    [Fact]
    public void SynthesizeBottleneckAttribution_WithDifferentPlanStructures_GeneratesTruthfulDynamicSummaries()
    {
        // Scenario A: Parallel Gather Merge with quicksort and Incremental Sort, measured in-process delta within jitter (-2.5ms)
        var semNodeA = new SanitizedPlanNode(
            NodeType: "Gather Merge",
            RelationName: null,
            IndexName: null,
            ParentRelationship: null,
            JoinType: null,
            SortMethod: "quicksort",
            SortSpaceType: "Memory",
            SortSpaceUsedKb: 128,
            PeakMemoryUsageKb: null,
            ParallelAware: true,
            AsyncCapable: false,
            StartupCost: 50.0,
            TotalCost: 800.0,
            PlanRows: 201,
            PlanWidth: 768,
            ActualStartupTimeMs: 5.0,
            ActualTotalTimeMs: 180.0,
            ActualRows: 201,
            ActualLoops: 1.0,
            SharedHitBlocks: 10000,
            SharedReadBlocks: 2000,
            SharedDirtiedBlocks: 0,
            SharedWrittenBlocks: 0,
            TempReadBlocks: 0,
            TempWrittenBlocks: 0,
            ChildPlans: [
                new SanitizedPlanNode(
                    NodeType: "Incremental Sort",
                    RelationName: null,
                    IndexName: null,
                    ParentRelationship: "Outer",
                    JoinType: null,
                    SortMethod: "quicksort",
                    SortSpaceType: "Memory",
                    SortSpaceUsedKb: 64,
                    PeakMemoryUsageKb: null,
                    ParallelAware: false,
                    AsyncCapable: false,
                    StartupCost: 10.0,
                    TotalCost: 300.0,
                    PlanRows: 201,
                    PlanWidth: 768,
                    ActualStartupTimeMs: 1.0,
                    ActualTotalTimeMs: 150.0,
                    ActualRows: 201,
                    ActualLoops: 1.0,
                    SharedHitBlocks: 8000,
                    SharedReadBlocks: 1000,
                    SharedDirtiedBlocks: 0,
                    SharedWrittenBlocks: 0,
                    TempReadBlocks: 0,
                    TempWrittenBlocks: 0,
                    ChildPlans: [])
            ]);

        var queryA = new QueryProfileResult(
            Role: "Semantic Candidates",
            DatabaseExecutionTimeMs: 180.0,
            DatabasePlanningTimeMs: 1.0,
            ResultRows: 201,
            TotalPlanNodes: 2,
            SharedHitBlocks: 10000,
            SharedReadBlocks: 2000,
            TempBlocks: 0,
            HasSortSpill: false,
            SortMethod: "quicksort",
            SortSpaceUsedKb: 128,
            PrimaryNodeSummary: "Gather Merge",
            Plan: semNodeA);

        var scenarioA = new ScenarioProfileResult(
            ScenarioId: "PLAN-A",
            Description: "Plan A",
            FilterEligibleCount: 50000,
            SemanticEligibleCount: 50000,
            FusedResultCount: 399,
            HybridHandlerLatency: new ScenarioLatencyMetrics(20, 202.0, 200.0, 210.0, 190.0, 220.0),
            HybridStoreLatency: new ScenarioLatencyMetrics(20, 204.5, 202.5, 212.0, 192.0, 225.0),
            LexicalFallbackHandlerLatency: new ScenarioLatencyMetrics(0, 0, 0, 0, 0, 0),
            LexicalFallbackStoreLatency: new ScenarioLatencyMetrics(0, 0, 0, 0, 0, 0),
            HybridQueries: [queryA],
            LexicalFallbackQueries: [],
            DominantBottleneckSummary: "Semantic candidate retrieval dominates DB time");

        // Act A
        var summaryA = SearchSqlProfiler.SynthesizeBottleneckAttribution([scenarioA]);

        // Assert A: Reflects Gather Merge, quicksort, Incremental Sort, and descriptive p50 delta range
        summaryA.Should().Contain("Gather Merge");
        summaryA.Should().Contain("Incremental Sort");
        summaryA.Should().Contain("quicksort");
        summaryA.Should().Contain("descriptive p50 delta range");
        summaryA.Should().Contain("In-process handler overhead attribution: unknown");
        summaryA.Should().NotContain("negligible");
        summaryA.Should().NotContain("measurement jitter");
        summaryA.Should().NotContain("Top-N Heapsort");
        summaryA.Should().NotContain("accounts for 2-5 ms");

        // Scenario B: Pure sequential scan with top-N heapsort, disk sort spill, and unmeasured handler (0 samples)
        var semNodeB = new SanitizedPlanNode(
            NodeType: "Seq Scan",
            RelationName: "asset_embeddings",
            IndexName: null,
            ParentRelationship: null,
            JoinType: null,
            SortMethod: "top-N heapsort",
            SortSpaceType: "Disk",
            SortSpaceUsedKb: 512,
            PeakMemoryUsageKb: null,
            ParallelAware: false,
            AsyncCapable: false,
            StartupCost: 0.0,
            TotalCost: 900.0,
            PlanRows: 201,
            PlanWidth: 768,
            ActualStartupTimeMs: 0.2,
            ActualTotalTimeMs: 250.0,
            ActualRows: 201,
            ActualLoops: 1.0,
            SharedHitBlocks: 2000,
            SharedReadBlocks: 8000,
            SharedDirtiedBlocks: 0,
            SharedWrittenBlocks: 0,
            TempReadBlocks: 16,
            TempWrittenBlocks: 16,
            ChildPlans: []);

        var queryB = new QueryProfileResult(
            Role: "Semantic Candidates",
            DatabaseExecutionTimeMs: 250.0,
            DatabasePlanningTimeMs: 0.5,
            ResultRows: 201,
            TotalPlanNodes: 1,
            SharedHitBlocks: 2000,
            SharedReadBlocks: 8000,
            TempBlocks: 32,
            HasSortSpill: true,
            SortMethod: "top-N heapsort",
            SortSpaceUsedKb: 512,
            PrimaryNodeSummary: "Seq Scan on asset_embeddings",
            Plan: semNodeB);

        var scenarioB = new ScenarioProfileResult(
            ScenarioId: "PLAN-B",
            Description: "Plan B",
            FilterEligibleCount: 50000,
            SemanticEligibleCount: 50000,
            FusedResultCount: 201,
            HybridHandlerLatency: new ScenarioLatencyMetrics(0, 0, 0, 0, 0, 0),
            HybridStoreLatency: new ScenarioLatencyMetrics(0, 0, 0, 0, 0, 0),
            LexicalFallbackHandlerLatency: new ScenarioLatencyMetrics(0, 0, 0, 0, 0, 0),
            LexicalFallbackStoreLatency: new ScenarioLatencyMetrics(0, 0, 0, 0, 0, 0),
            HybridQueries: [queryB],
            LexicalFallbackQueries: [],
            DominantBottleneckSummary: "Semantic candidate retrieval dominates DB time");

        // Act B
        var summaryB = SearchSqlProfiler.SynthesizeBottleneckAttribution([scenarioB]);

        // Assert B: Reflects Seq Scan, top-N heapsort, DISK SPILL DETECTED, and unmeasured overhead
        summaryB.Should().Contain("Seq Scan");
        summaryB.Should().Contain("top-N heapsort");
        summaryB.Should().Contain("DISK SPILL DETECTED");
        summaryB.Should().Contain("In-process handler overhead: unknown");
        summaryB.Should().NotContain("Gather Merge");
        summaryB.Should().NotContain("accounts for 2-5 ms");
    }

    [Fact]
    public void ComputeSourceFingerprint_WhenFileModified_ProducesDifferentCryptographicFingerprint()
    {
        // Arrange: Create a temporary directory with test source files
        var tempDir = Path.Combine(Path.GetTempPath(), "asblock_test_fp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var srcFile = Path.Combine(tempDir, "SearchService.cs");
            File.WriteAllText(srcFile, "// Initial source code\npublic class SearchService {}");

            // Act 1: Compute initial fingerprint
            var fp1 = SearchEvaluationDbFixture.ComputeSourceFingerprint(tempDir);

            // Assert 1: Valid fingerprint produced
            fp1.Should().NotBeNullOrWhiteSpace();
            fp1.Should().NotBe("unknown");
            fp1.Should().Contain("-src:");

            // Act 2: Modify source file (e.g. untracked edit)
            File.WriteAllText(srcFile, "// Modified source code with new behavior\npublic class SearchService { void Method() {} }");
            var fp2 = SearchEvaluationDbFixture.ComputeSourceFingerprint(tempDir);

            // Assert 2: Fingerprint changed
            fp2.Should().NotBe(fp1);

            // Act 3: Verify non-existent directory returns "unknown"
            var fpUnknown = SearchEvaluationDbFixture.ComputeSourceFingerprint(Path.Combine(tempDir, "nonexistent"));
            fpUnknown.Should().Be("unknown");
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
    public void ValidateProfilingArguments_ValidatesSizesConcurrencyWarmupAndSamples()
    {
        // Valid arguments: [50000], warmup 5, samples 20, [1]
        Action actValid = () => SearchSqlProfiler.ValidateProfilingArguments([50000], 5, 20, [1]);
        actValid.Should().NotThrow();

        // Unsupported sizes
        Action actInvalidSize = () => SearchSqlProfiler.ValidateProfilingArguments([1000, 50000], 5, 20, [1]);
        actInvalidSize.Should().Throw<ArgumentException>()
            .WithMessage("*supports only a single corpus size of 50000*");

        // Unsupported concurrency
        Action actInvalidConcurrency = () => SearchSqlProfiler.ValidateProfilingArguments([50000], 5, 20, [1, 10]);
        actInvalidConcurrency.Should().Throw<ArgumentException>()
            .WithMessage("*sequential single-threaded diagnostic mode supporting only concurrency=1*");

        Action actConcurrency10 = () => SearchSqlProfiler.ValidateProfilingArguments([50000], 5, 20, [10]);
        actConcurrency10.Should().Throw<ArgumentException>()
            .WithMessage("*sequential single-threaded diagnostic mode supporting only concurrency=1*");

        // Negative warmup
        Action actNegativeWarmup = () => SearchSqlProfiler.ValidateProfilingArguments([50000], -1, 20, [1]);
        actNegativeWarmup.Should().Throw<ArgumentException>()
            .WithMessage("*Warmup count must be non-negative*");

        // Zero samples
        Action actZeroSamples = () => SearchSqlProfiler.ValidateProfilingArguments([50000], 5, 0, [1]);
        actZeroSamples.Should().Throw<ArgumentException>()
            .WithMessage("*Sample count must be positive*");
    }

    [Fact]
    public async Task GetAssetsQueryHandler_WhenVectorCapabilityDisabled_ExecutesRelevanceFallbackWithoutExplicitSort()
    {
        // Arrange: Store mock, disabled capability, search query without explicit sort
        IAssetStore store = Substitute.For<IAssetStore>();
        ITypedCache cache = Substitute.For<ITypedCache>();
        IQueryVectorCache queryVectorCache = Substitute.For<IQueryVectorCache>();
        var capability = new BenchmarkVectorSearchCapability("model-key", isAvailable: false);
        ITextEmbeddingGenerator generator = Substitute.For<ITextEmbeddingGenerator>();

        var handler = new GetAssetsQueryHandler(
            store,
            cache,
            queryVectorCache,
            capability,
            generator,
            Microsoft.Extensions.Options.Options.Create(new EmbeddingOptions { Model = "test", Dimension = 768 }),
            NullLogger<GetAssetsQueryHandler>.Instance);

        var request = new GetAssetsRequest
        {
            Search = "sword shield",
            SortBy = null, // Relevance fallback query
            Page = 1,
            PageSize = 20
        };

        store.GetPaged(
                Arg.Any<GetAssetsRequest>(),
                Arg.Any<float[]?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(new CatalogPageResult<AssetListItem>([], 0, 1, 20));

        // Act
        Result<CatalogPageResult<AssetListItem>> result = await handler.Handle(new GetAssetsQuery(request), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // Must delegate to store.GetPaged with identical request (SortBy == null) and null query vector
        await store.Received(1).GetPaged(
            Arg.Is<GetAssetsRequest>(r => r.Search == "sword shield" && r.SortBy == null),
            null,
            null,
            Arg.Any<CancellationToken>());

        // Generator must never be called on disabled capability fallback
        await generator.DidNotReceiveWithAnyArgs().Generate(null!, CancellationToken.None);
    }

    [Fact]
    public void ScenarioProfileResult_WhenFilterEligibleExceedsWindow_PreservesTrueSelectivity()
    {
        // Arrange: Corpus has 50,000 filter-eligible assets, but fused RRF window returns 399 items
        var scenario = new ScenarioProfileResult(
            ScenarioId: "unfiltered",
            Description: "Unfiltered search",
            FilterEligibleCount: 50000,
            SemanticEligibleCount: 50000,
            FusedResultCount: 399,
            HybridHandlerLatency: new ScenarioLatencyMetrics(1, 200.0, 200.0, 200.0, 200.0, 200.0),
            HybridStoreLatency: new ScenarioLatencyMetrics(1, 200.0, 200.0, 200.0, 200.0, 200.0),
            LexicalFallbackHandlerLatency: new ScenarioLatencyMetrics(1, 100.0, 100.0, 100.0, 100.0, 100.0),
            LexicalFallbackStoreLatency: new ScenarioLatencyMetrics(1, 100.0, 100.0, 100.0, 100.0, 100.0),
            HybridQueries: [],
            LexicalFallbackQueries: [],
            DominantBottleneckSummary: "Sequential scan");

        // Assert: FilterEligibleCount is not capped or reduced by FusedResultCount
        scenario.FilterEligibleCount.Should().Be(50000);
        scenario.SemanticEligibleCount.Should().Be(50000);
        scenario.FusedResultCount.Should().Be(399);
    }

    [Fact]
    public void SqlProfilingInterceptor_WhenCommandExecuted_ClonesTypedParametersSafely()
    {
        // Arrange
        var interceptor = new SqlProfilingInterceptor();
        using var cmd = new NpgsqlCommand("SELECT * FROM assets WHERE id = @p0 AND price > @p1");
        cmd.Parameters.Add(new NpgsqlParameter("@p0", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
        cmd.Parameters.Add(new NpgsqlParameter("@p1", NpgsqlDbType.Numeric) { Value = 19.99m });

        // Act
        interceptor.CaptureCommand(cmd);
        IReadOnlyList<CapturedDbCommand> captured = interceptor.GetCapturedCommands();

        // Assert
        captured.Should().HaveCount(1);
        captured[0].CommandText.Should().Be("SELECT * FROM assets WHERE id = @p0 AND price > @p1");
        captured[0].Parameters.Should().HaveCount(2);
        captured[0].Parameters[0].ParameterName.Should().Be("@p0");
        captured[0].Parameters[0].NpgsqlDbType.Should().Be(NpgsqlDbType.Uuid);
        captured[0].Parameters[1].ParameterName.Should().Be("@p1");
        captured[0].Parameters[1].NpgsqlDbType.Should().Be(NpgsqlDbType.Numeric);
    }

    [Fact]
    public async Task ReplayExplainAsync_WhenCancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await using var connection = new NpgsqlConnection("Host=localhost;Database=test;Username=postgres;Password=postgres");
        var captured = new CapturedDbCommand("SELECT 1", []);

        // Act
        Func<Task> act = async () => await SqlProfilingInterceptor.ReplayExplainAsync(connection, captured, cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void SynthesizeBottleneckAttribution_WhenLargeNegativeDeltaObserved_DoesNotClaimNegligibleOrMeasurementJitter()
    {
        // Arrange: Scenario where handler p50 is 100ms and store p50 is 200ms (delta is -100ms)
        var semNode = new SanitizedPlanNode(
            NodeType: "Seq Scan",
            RelationName: "asset_embeddings",
            IndexName: null,
            ParentRelationship: null,
            JoinType: null,
            SortMethod: "quicksort",
            SortSpaceType: "Memory",
            SortSpaceUsedKb: 32,
            PeakMemoryUsageKb: null,
            ParallelAware: false,
            AsyncCapable: false,
            StartupCost: 0.0,
            TotalCost: 100.0,
            PlanRows: 201,
            PlanWidth: 768,
            ActualStartupTimeMs: 0.1,
            ActualTotalTimeMs: 120.0,
            ActualRows: 201,
            ActualLoops: 1.0,
            SharedHitBlocks: 1000,
            SharedReadBlocks: 0,
            SharedDirtiedBlocks: 0,
            SharedWrittenBlocks: 0,
            TempReadBlocks: 0,
            TempWrittenBlocks: 0,
            ChildPlans: []);

        var query = new QueryProfileResult(
            Role: "Semantic Candidates",
            DatabaseExecutionTimeMs: 120.0,
            DatabasePlanningTimeMs: 0.5,
            ResultRows: 201,
            TotalPlanNodes: 1,
            SharedHitBlocks: 1000,
            SharedReadBlocks: 0,
            TempBlocks: 0,
            HasSortSpill: false,
            SortMethod: "quicksort",
            SortSpaceUsedKb: 32,
            PrimaryNodeSummary: "Seq Scan",
            Plan: semNode);

        var scenario = new ScenarioProfileResult(
            ScenarioId: "NEGATIVE-DELTA",
            Description: "Large negative delta scenario",
            FilterEligibleCount: 50000,
            SemanticEligibleCount: 50000,
            FusedResultCount: 201,
            HybridHandlerLatency: new ScenarioLatencyMetrics(10, 100.0, 100.0, 100.0, 100.0, 100.0),
            HybridStoreLatency: new ScenarioLatencyMetrics(10, 200.0, 200.0, 200.0, 200.0, 200.0),
            LexicalFallbackHandlerLatency: new ScenarioLatencyMetrics(0, 0, 0, 0, 0, 0),
            LexicalFallbackStoreLatency: new ScenarioLatencyMetrics(0, 0, 0, 0, 0, 0),
            HybridQueries: [query],
            LexicalFallbackQueries: [],
            DominantBottleneckSummary: "Semantic candidate retrieval");

        // Act
        var summary = SearchSqlProfiler.SynthesizeBottleneckAttribution([scenario]);

        // Assert: Must describe the numerical delta, label overhead attribution as unknown, and NEVER claim negligible or measurement jitter
        summary.Should().Contain("-100.00 ms");
        summary.Should().Contain("In-process handler overhead attribution: unknown");
        summary.Should().NotContain("negligible");
        summary.Should().NotContain("measurement jitter");
    }

    [Fact]
    public void ValidateHybridHandlerExecution_WhenCacheMissOrProviderCallOccurs_ThrowsInvalidOperationException()
    {
        // Arrange: Hybrid retrieval executed (HybridInvocations = 1), but provider call occurred due to cache miss
        var dummyResult = Result.Success(new CatalogPageResult<AssetListItem>([], 0, 1, 20));
        var tracker = new SearchBenchmarkRunner.BenchmarkExecutionTracker();
        tracker.RecordHybridInvocation();
        tracker.RecordProviderCall(); // simulate provider call on cache miss
        var generator = new SearchBenchmarkRunner.CachedVectorOnlyEmbeddingGenerator(tracker);

        // Act
        Action act = () => SearchSqlProfiler.ValidateHybridHandlerExecution(dummyResult, tracker, generator);

        // Assert: Must specifically fail due to provider call violation
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*expected 1 hybrid invocation, 0 lexical invocations, and 0 provider calls*")
            .WithMessage("*providerCalls=1*")
            .WithMessage("*Profiling evidence is invalid*");
    }

    [Fact]
    public void ValidateHybridHandlerExecution_WhenHybridFailsAndFallsBackToLexical_ThrowsInvalidOperationException()
    {
        // Arrange: Handler caught a hybrid failure and completed lexical fallback successfully
        var dummyResult = Result.Success(new CatalogPageResult<AssetListItem>([], 0, 1, 20));
        var tracker = new SearchBenchmarkRunner.BenchmarkExecutionTracker();
        tracker.RecordHybridInvocation();
        tracker.RecordLexicalInvocation(); // Lexical fallback executed!
        var generator = new SearchBenchmarkRunner.CachedVectorOnlyEmbeddingGenerator(tracker);

        // Act
        Action act = () => SearchSqlProfiler.ValidateHybridHandlerExecution(dummyResult, tracker, generator);

        // Assert: Must reject profiling evidence as invalid because hybrid path experienced hidden fallback
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Profiling evidence is invalid*")
            .WithMessage("*invariant violated*");
    }
}
