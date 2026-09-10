#pragma warning disable CA5394 // Insecure random number generator in deterministic synthetic evaluation seed
using System.Diagnostics;
using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.UseCases.Assets.GetAssets;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Entities;
using AssetBlock.Infrastructure.Persistence.Interceptors;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using AssetBlock.SearchEvaluation.Benchmark;
using AssetBlock.SearchEvaluation.Evaluation;
using AssetBlock.SearchEvaluation.Infrastructure;
using AssetBlock.SearchEvaluation.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;

namespace AssetBlock.SearchEvaluation.Profiling;

public static class SearchSqlProfiler
{
    private const string MEASUREMENT_SCOPE =
        "Direct in-process handler and AssetStore execution with isolated PostgreSQL query timing; excludes HTTP transport, network serialization, ASP.NET Core middleware, authentication, and frontend roundtrip.";

    public static void ValidateProfilingArguments(
        IReadOnlyList<int> corpusSizes,
        int warmupCount,
        int sampleCount,
        IReadOnlyList<int> concurrencyLevels)
    {
        if (corpusSizes is not [50000])
        {
            throw new ArgumentException(
                $"Isolated SQL profiling currently supports only a single corpus size of 50000. Specified sizes: [{(corpusSizes != null ? string.Join(", ", corpusSizes) : "")}] are not supported.",
                nameof(corpusSizes));
        }

        if (concurrencyLevels is not [1])
        {
            throw new ArgumentException(
                $"Isolated SQL profiling is a sequential single-threaded diagnostic mode supporting only concurrency=1. Specified concurrency: [{(concurrencyLevels != null ? string.Join(", ", concurrencyLevels) : "")}] is not supported.",
                nameof(concurrencyLevels));
        }

        if (warmupCount < 0)
        {
            throw new ArgumentException($"Warmup count must be non-negative, but was {warmupCount}.", nameof(warmupCount));
        }

        if (sampleCount <= 0)
        {
            throw new ArgumentException($"Sample count must be positive, but was {sampleCount}.", nameof(sampleCount));
        }
    }

    public static async Task<int> RunProfilingAsync(
        List<int> corpusSizes,
        int warmupCount,
        int sampleCount,
        List<int> concurrencyLevels,
        bool skipOllama,
        EmbeddingOptions embeddingOptions,
        CancellationToken cancellationToken = default)
    {
        ValidateProfilingArguments(corpusSizes, warmupCount, sampleCount, concurrencyLevels);

        Console.WriteLine("==========================================================");
        Console.WriteLine(" AssetBlock Isolated SQL Profiling Runner");
        Console.WriteLine("==========================================================");
        Console.WriteLine($"Corpus Size:      50,000 assets (with negative fixtures)");
        Console.WriteLine($"Diagnostic Reps:  Warmup={warmupCount}, Samples={sampleCount}, Concurrency={concurrencyLevels.FirstOrDefault()}");
        Console.WriteLine($"Skip Ollama:      {skipOllama} (Exploratory DB-only diagnostic)");
        Console.WriteLine($"Pinned Model:     {embeddingOptions.Model}");
        Console.WriteLine($"Dimension:        {embeddingOptions.Dimension}");
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine();

        var modelKey = EmbeddingModelKey.Compute(embeddingOptions);

        Console.WriteLine("--> Initializing isolated disposable pgvector Testcontainer...");
        await using var fixture = new SearchEvaluationDbFixture();
        await fixture.InitializeAsync(cancellationToken);

        SystemEnvironmentProvenance provenance = await fixture.CollectProvenance(cancellationToken);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[PASS] Isolated PostgreSQL container initialized.");
        Console.WriteLine($"  PostgreSQL:     {provenance.PostgresVersion}");
        Console.WriteLine($"  pgvector:       {provenance.PgVectorVersion}");
        Console.WriteLine($"  Container:      {provenance.ContainerImageDigest}");
        Console.WriteLine($"  HNSW state:     {provenance.HnswState}");
        Console.WriteLine($"  Fingerprint:    {provenance.SourceFingerprint ?? provenance.GitCommit}");
        Console.ResetColor();
        Console.WriteLine();

        // 1. Seed base authors, categories, tags
        Guid authorId;
        Guid secondaryAuthorId;
        var categoryIds = new List<Guid>();
        var tagIds = new List<Guid>();

        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            var author = new User
            {
                Id = Guid.NewGuid(),
                Username = "profiling_author",
                Email = "profiling_author@example.com",
                PasswordHash = "hash",
                Role = "SELLER",
                CreatedAt = DateTimeOffset.UtcNow
            };
            var secondaryAuthor = new User
            {
                Id = Guid.NewGuid(),
                Username = "secondary_author",
                Email = "secondary_author@example.com",
                PasswordHash = "hash",
                Role = "SELLER",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Users.AddRange(author, secondaryAuthor);
            authorId = author.Id;
            secondaryAuthorId = secondaryAuthor.Id;

            var categories = new[] { "3D Models", "Textures", "Audio", "Scripts", "VFX" };
            foreach (var catName in categories)
            {
                var cat = new Category
                {
                    Id = Guid.NewGuid(),
                    Name = catName,
                    Slug = catName.ToLowerInvariant().Replace(' ', '-'),
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.Categories.Add(cat);
                categoryIds.Add(cat.Id);
            }

            var tags = new[] { "medieval", "weapon", "fantasy", "sci-fi", "pbr" };
            foreach (var tagName in tags)
            {
                var tag = new Tag
                {
                    Id = Guid.NewGuid(),
                    Name = tagName,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.Tags.Add(tag);
                tagIds.Add(tag.Id);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        // 2. Seed 50,000 synthetic assets + negative fixtures
        const int targetTotalAssets = 50000;
        Console.WriteLine($"--> Seeding {targetTotalAssets:N0} assets with positive and negative fixtures...");
        var seedSw = Stopwatch.StartNew();
        Dictionary<string, int> negativeFixtureCounts = await SeedCorpusAndFixturesAsync(
            fixture,
            targetTotalAssets,
            authorId,
            secondaryAuthorId,
            categoryIds,
            tagIds,
            modelKey,
            embeddingOptions,
            cancellationToken);
        seedSw.Stop();

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[PASS] Corpus seeded in {seedSw.Elapsed.TotalSeconds:F1}s.");
        Console.WriteLine("  Negative fixtures seeded:");
        foreach ((var k, var v) in negativeFixtureCounts)
        {
            Console.WriteLine($"    - {k,-35}: {v}");
        }
        Console.ResetColor();
        Console.WriteLine();

        // 3. Update planner statistics on disposable DB
        Console.WriteLine("--> Refreshing planner statistics via ANALYZE...");
        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("ANALYZE;", cancellationToken);
        }
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[PASS] Planner statistics refreshed.");
        Console.ResetColor();
        Console.WriteLine();

        // 4. Define 7 bounded synthetic scenarios
        List<ScenarioDefinition> scenarios = DefineScenarios(authorId, categoryIds[0], ["medieval", "weapon"]);
        var scenarioResults = new List<ScenarioProfileResult>();

        Console.WriteLine($"--> Running SQL profiling for {scenarios.Count} bounded scenarios...");
        Console.WriteLine();

        foreach (ScenarioDefinition scenarioDef in scenarios)
        {
            Console.WriteLine($"--- Scenario: {scenarioDef.Id} ({scenarioDef.Description}) ---");
            ScenarioProfileResult profileResult = await ProfileScenarioAsync(
                fixture,
                scenarioDef,
                warmupCount,
                sampleCount,
                modelKey,
                embeddingOptions,
                cancellationToken);

            scenarioResults.Add(profileResult);

            Console.WriteLine($"  Filter-Eligible: {profileResult.FilterEligibleCount:N0} | Semantic-Eligible: {profileResult.SemanticEligibleCount:N0} | Fused: {profileResult.FusedResultCount:N0}");
            Console.WriteLine($"  Hybrid Handler:  p50={profileResult.HybridHandlerLatency.P50Ms:F1}ms, p95={profileResult.HybridHandlerLatency.P95Ms:F1}ms | Store: p50={profileResult.HybridStoreLatency.P50Ms:F1}ms, p95={profileResult.HybridStoreLatency.P95Ms:F1}ms");
            Console.WriteLine($"  Lexical Handler: p50={profileResult.LexicalFallbackHandlerLatency.P50Ms:F1}ms, p95={profileResult.LexicalFallbackHandlerLatency.P95Ms:F1}ms | Store: p50={profileResult.LexicalFallbackStoreLatency.P50Ms:F1}ms, p95={profileResult.LexicalFallbackStoreLatency.P95Ms:F1}ms");
            Console.WriteLine($"  Timing note:    {profileResult.DominantBottleneckSummary}");
            Console.WriteLine();
        }

        // 5. Synthesize observed timing summary (no causal ANN/HNSW recommendation)
        var attributionSummary = SynthesizeBottleneckAttribution(scenarioResults);

        var reportData = new SqlProfileReportData(
            provenance,
            embeddingOptions,
            warmupCount,
            sampleCount,
            concurrencyLevels[0],
            scenarioResults,
            negativeFixtureCounts,
            provenance.HnswState,
            MEASUREMENT_SCOPE,
            attributionSummary);

        (var jsonPath, var mdPath) = SearchEvaluationReportWriter.WriteSqlProfileReport(reportData);
        Console.WriteLine("==========================================================");
        Console.WriteLine(" SQL Profiling Completed");
        Console.WriteLine("==========================================================");
        Console.WriteLine(attributionSummary);
        Console.WriteLine();
        Console.WriteLine("Reports emitted:");
        Console.WriteLine($"  - JSON:     {jsonPath}");
        Console.WriteLine($"  - Markdown: {mdPath}");
        Console.WriteLine("==========================================================");

        return Program.EXIT_SUCCESS;
    }

    private sealed record ScenarioDefinition(
        string Id,
        string Description,
        GetAssetsRequest Request);

    private static List<ScenarioDefinition> DefineScenarios(
        Guid authorId,
        Guid categoryId,
        List<string> sampleTags)
    {
        return
        [
            new(
                "unfiltered",
                "Unfiltered search with relevance sorting",
                new GetAssetsRequest { Search = "sword shield", Page = 1, PageSize = 20 }),

            new(
                "category",
                "Filtered by exact category",
                new GetAssetsRequest { Search = "sword shield", CategoryId = categoryId, Page = 1, PageSize = 20 }),

            new(
                "author",
                "Filtered by exact author",
                new GetAssetsRequest { Search = "sword shield", AuthorId = authorId, Page = 1, PageSize = 20 }),

            new(
                "price",
                "Filtered by price range ($10 - $30)",
                new GetAssetsRequest { Search = "sword shield", MinPrice = 10m, MaxPrice = 30m, Page = 1, PageSize = 20 }),

            new(
                "all-tags",
                "Filtered by multiple tags with all-tags match semantics",
                new GetAssetsRequest { Search = "sword shield", Tags = sampleTags, Page = 1, PageSize = 20 }),

            new(
                "combined selective",
                "Combined selective filters (category + author + price + tag)",
                new GetAssetsRequest
                {
                    Search = "sword shield",
                    CategoryId = categoryId,
                    AuthorId = authorId,
                    MinPrice = 10m,
                    MaxPrice = 30m,
                    Tags = ["medieval"],
                    Page = 1,
                    PageSize = 20
                }),

            new(
                "zero eligible",
                "Selective filters with zero eligible assets",
                new GetAssetsRequest
                {
                    Search = "sword shield",
                    MinPrice = 99999m,
                    MaxPrice = 100000m,
                    Page = 1,
                    PageSize = 20
                })
        ];
    }

    private static async Task<ScenarioProfileResult> ProfileScenarioAsync(
        SearchEvaluationDbFixture fixture,
        ScenarioDefinition scenarioDef,
        int warmupCount,
        int sampleCount,
        string modelKey,
        EmbeddingOptions embeddingOptions,
        CancellationToken cancellationToken)
    {
        var queryText = scenarioDef.Request.Search!;
        var queryVector = GenerateDeterministicVector(queryText, embeddingOptions.Dimension);

        var queryVectorCache = new BoundedQueryVectorCache(TimeProvider.System, maxEntries: 1000);
        var normalizedQuery = CatalogSearchNormalization.NormalizeSearchQuery(queryText);
        var searchHash = CacheKeys.HashSearchQuery(normalizedQuery);
        queryVectorCache.Set(modelKey, searchHash, queryVector, TimeSpan.FromHours(1));

        ITypedCache resultCache = new SearchBenchmarkRunner.ObservableResultCache();
        var capability = new BenchmarkVectorSearchCapability(modelKey, isAvailable: true);
        var fallbackCapability = new BenchmarkVectorSearchCapability(modelKey, isAvailable: false);

        // 1. Warm-up
        for (var i = 0; i < warmupCount; i++)
        {
            await using ApplicationDbContext db = fixture.CreateDbContext();
            var store = new AssetStore(db);

            var warmupHybridTracker = new SearchBenchmarkRunner.BenchmarkExecutionTracker();
            var observingWarmupStore = new SearchBenchmarkRunner.EvaluationObservingAssetStore(store, warmupHybridTracker);
            var warmupHybridGenerator = new SearchBenchmarkRunner.CachedVectorOnlyEmbeddingGenerator(warmupHybridTracker);

            var handler = new GetAssetsQueryHandler(
                observingWarmupStore,
                resultCache,
                queryVectorCache,
                capability,
                warmupHybridGenerator,
                Options.Create(embeddingOptions),
                NullLogger<GetAssetsQueryHandler>.Instance);

            var warmupFallbackTracker = new SearchBenchmarkRunner.BenchmarkExecutionTracker();
            var observingFallbackStore = new SearchBenchmarkRunner.EvaluationObservingAssetStore(store, warmupFallbackTracker);
            var warmupFallbackGenerator = new SearchBenchmarkRunner.CachedVectorOnlyEmbeddingGenerator(warmupFallbackTracker);

            var fallbackHandler = new GetAssetsQueryHandler(
                observingFallbackStore,
                resultCache,
                queryVectorCache,
                fallbackCapability,
                warmupFallbackGenerator,
                Options.Create(embeddingOptions),
                NullLogger<GetAssetsQueryHandler>.Instance);

            await handler.Handle(new GetAssetsQuery(scenarioDef.Request), cancellationToken);
            await store.GetPaged(scenarioDef.Request, queryVector, modelKey, cancellationToken);
            await fallbackHandler.Handle(new GetAssetsQuery(scenarioDef.Request), cancellationToken);
            await store.GetPaged(scenarioDef.Request, null, null, cancellationToken);
        }

        // 2. Measured repetitions (Direct Store vs Direct Handler)
        var hybridHandlerLatencies = new List<double>(sampleCount);
        var hybridStoreLatencies = new List<double>(sampleCount);
        var lexicalHandlerLatencies = new List<double>(sampleCount);
        var lexicalStoreLatencies = new List<double>(sampleCount);
        var fusedResultCount = 0;

        var sw = new Stopwatch();

        for (var i = 0; i < sampleCount; i++)
        {
            await using ApplicationDbContext db = fixture.CreateDbContext();
            var store = new AssetStore(db);

            // Handler hybrid (must execute hybrid retrieval, 0 lexical invocations, 0 provider/generator calls)
            var hybridTracker = new SearchBenchmarkRunner.BenchmarkExecutionTracker();
            var observingHybridStore = new SearchBenchmarkRunner.EvaluationObservingAssetStore(store, hybridTracker);
            var hybridGenerator = new SearchBenchmarkRunner.CachedVectorOnlyEmbeddingGenerator(hybridTracker);

            var handler = new GetAssetsQueryHandler(
                observingHybridStore,
                resultCache,
                queryVectorCache,
                capability,
                hybridGenerator,
                Options.Create(embeddingOptions),
                NullLogger<GetAssetsQueryHandler>.Instance);

            sw.Restart();
            Result<CatalogPageResult<AssetListItem>> hRes = await handler.Handle(new GetAssetsQuery(scenarioDef.Request), cancellationToken);
            sw.Stop();
            hybridHandlerLatencies.Add(sw.Elapsed.TotalMilliseconds);

            ValidateHybridHandlerExecution(hRes, hybridTracker, hybridGenerator);

            fusedResultCount = hRes.Value.TotalCount;

            // Store hybrid
            sw.Restart();
            await store.GetPaged(scenarioDef.Request, queryVector, modelKey, cancellationToken);
            sw.Stop();
            hybridStoreLatencies.Add(sw.Elapsed.TotalMilliseconds);

            // Store lexical fallback
            sw.Restart();
            await store.GetPaged(scenarioDef.Request, null, null, cancellationToken);
            sw.Stop();
            lexicalStoreLatencies.Add(sw.Elapsed.TotalMilliseconds);

            // Handler lexical fallback (identical request; fallback triggered via unavailable capability)
            var fallbackTracker = new SearchBenchmarkRunner.BenchmarkExecutionTracker();
            var observingFallbackStore = new SearchBenchmarkRunner.EvaluationObservingAssetStore(store, fallbackTracker);
            var fallbackGenerator = new SearchBenchmarkRunner.CachedVectorOnlyEmbeddingGenerator(fallbackTracker);

            var fallbackHandler = new GetAssetsQueryHandler(
                observingFallbackStore,
                resultCache,
                queryVectorCache,
                fallbackCapability,
                fallbackGenerator,
                Options.Create(embeddingOptions),
                NullLogger<GetAssetsQueryHandler>.Instance);

            sw.Restart();
            Result<CatalogPageResult<AssetListItem>> lhRes = await fallbackHandler.Handle(new GetAssetsQuery(scenarioDef.Request), cancellationToken);
            sw.Stop();
            lexicalHandlerLatencies.Add(sw.Elapsed.TotalMilliseconds);

            ValidateLexicalFallbackHandlerExecution(lhRes, fallbackTracker, fallbackGenerator);
        }

        // Compute true filter and semantic selectivity counts outside timing loop
        (var filterEligibleCount, var semanticEligibleCount) = await CountEligibleAssetsAsync(
            fixture,
            scenarioDef.Request,
            modelKey,
            cancellationToken);

        if (scenarioDef.Id == "combined selective")
        {
            if (filterEligibleCount is <= 0 or >= 50000)
            {
                throw new InvalidOperationException(
                    $"Combined selective scenario invariant failed: expected 0 < eligible < 50000, but was {filterEligibleCount}.");
            }
        }
        else if (scenarioDef.Id == "zero eligible")
        {
            if (filterEligibleCount != 0)
            {
                throw new InvalidOperationException(
                    $"Zero eligible scenario invariant failed: expected 0, but was {filterEligibleCount}.");
            }
        }

        // 3. Isolated Parameterized EXPLAIN Replay (outside measured timing loop)
        var hybridQueries = new List<QueryProfileResult>();
        var lexicalQueries = new List<QueryProfileResult>();

        var interceptor = new SqlProfilingInterceptor();
        DbContextOptionsBuilder<ApplicationDbContext> optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .AddInterceptors(new AuditTimestampsInterceptor(TimeProvider.System), interceptor);

        await using (var profilingDb = new ApplicationDbContext(optionsBuilder.Options))
        {
            var profilingStore = new AssetStore(profilingDb);

            // Hybrid capture
            interceptor.Clear();
            await profilingStore.GetPaged(scenarioDef.Request, queryVector, modelKey, cancellationToken);
            IReadOnlyList<CapturedDbCommand> capturedHybridCommands = interceptor.GetCapturedCommands();

            await using NpgsqlConnection connection = fixture.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            foreach (CapturedDbCommand cmd in capturedHybridCommands)
            {
                var role = DetectQueryRole(cmd.CommandText, isHybrid: true);
                SanitizedExplainPlan plan = await SqlProfilingInterceptor.ReplayExplainAsync(connection, cmd, cancellationToken);

                var primarySummary = SummarizePrimaryNode(plan.RootNode);
                hybridQueries.Add(new QueryProfileResult(
                    role,
                    plan.ExecutionTimeMs,
                    plan.PlanningTimeMs,
                    (int)plan.RootNode.ActualRows,
                    plan.TotalNodesCount,
                    plan.TotalSharedHitBlocks,
                    plan.TotalSharedReadBlocks,
                    plan.TotalTempBlocks,
                    plan.HasSortSpill,
                    FindSortMethod(plan.RootNode),
                    FindSortSpaceUsedKb(plan.RootNode),
                    primarySummary,
                    plan.RootNode));
            }

            // Lexical fallback capture
            interceptor.Clear();
            await profilingStore.GetPaged(scenarioDef.Request, null, null, cancellationToken);
            IReadOnlyList<CapturedDbCommand> capturedLexicalCommands = interceptor.GetCapturedCommands();

            foreach (CapturedDbCommand cmd in capturedLexicalCommands)
            {
                var role = DetectQueryRole(cmd.CommandText, isHybrid: false);
                SanitizedExplainPlan plan = await SqlProfilingInterceptor.ReplayExplainAsync(connection, cmd, cancellationToken);

                var primarySummary = SummarizePrimaryNode(plan.RootNode);
                lexicalQueries.Add(new QueryProfileResult(
                    role,
                    plan.ExecutionTimeMs,
                    plan.PlanningTimeMs,
                    (int)plan.RootNode.ActualRows,
                    plan.TotalNodesCount,
                    plan.TotalSharedHitBlocks,
                    plan.TotalSharedReadBlocks,
                    plan.TotalTempBlocks,
                    plan.HasSortSpill,
                    FindSortMethod(plan.RootNode),
                    FindSortSpaceUsedKb(plan.RootNode),
                    primarySummary,
                    plan.RootNode));
            }
        }

        var bottleneckSummary = DetermineDominantBottleneck(hybridQueries);

        return new ScenarioProfileResult(
            scenarioDef.Id,
            scenarioDef.Description,
            filterEligibleCount,
            semanticEligibleCount,
            fusedResultCount,
            ComputeLatencyMetrics(hybridHandlerLatencies),
            ComputeLatencyMetrics(hybridStoreLatencies),
            ComputeLatencyMetrics(lexicalHandlerLatencies),
            ComputeLatencyMetrics(lexicalStoreLatencies),
            hybridQueries,
            lexicalQueries,
            bottleneckSummary);
    }

    private static string DetectQueryRole(string sql, bool isHybrid)
    {
        var lower = sql.ToLowerInvariant();
        if (isHybrid)
        {
            if (lower.Contains("asset_embeddings") || lower.Contains("<=>") || lower.Contains("cosinedistance"))
            {
                return "Semantic Candidates";
            }
            if (lower.Contains("websearch_to_tsquery") || lower.Contains("search_vector") || lower.Contains("similarity(") || (lower.Contains("ilike") && lower.Contains("limit")))
            {
                return "Hybrid Lexical Candidates";
            }
            return "Hybrid Page Hydration";
        }
        else
        {
            if (lower.Contains("count("))
            {
                return "Lexical Fallback Count";
            }
            if (lower.Contains("order by") && (lower.Contains("rank(") || lower.Contains("similarity") || lower.Contains("score")))
            {
                return "Lexical Fallback Ranking";
            }
            return "Lexical Fallback Hydration";
        }
    }

    private static string SummarizePrimaryNode(SanitizedPlanNode node)
    {
        SanitizedPlanNode target = FindDominantCostNode(node);
        var rel = target.RelationName != null ? $" on {target.RelationName}" : "";
        var idx = target.IndexName != null ? $" ({target.IndexName})" : "";
        return $"{target.NodeType}{rel}{idx} [Rows={target.ActualRows:F0}, Loops={target.ActualLoops:F0}, Time={target.ActualTotalTimeMs:F2}ms]";
    }

    private static SanitizedPlanNode FindDominantCostNode(SanitizedPlanNode node)
    {
        SanitizedPlanNode max = node;
        foreach (SanitizedPlanNode child in node.ChildPlans)
        {
            SanitizedPlanNode childMax = FindDominantCostNode(child);
            if (childMax.ActualTotalTimeMs > max.ActualTotalTimeMs)
            {
                max = childMax;
            }
        }
        return max;
    }

    private static string? FindSortMethod(SanitizedPlanNode node)
    {
        if (node.SortMethod != null)
        {
            return node.SortMethod;
        }
        foreach (SanitizedPlanNode child in node.ChildPlans)
        {
            var res = FindSortMethod(child);
            if (res != null)
            {
                return res;
            }
        }
        return null;
    }

    private static long? FindSortSpaceUsedKb(SanitizedPlanNode node)
    {
        if (node.SortSpaceUsedKb.HasValue)
        {
            return node.SortSpaceUsedKb.Value;
        }
        foreach (SanitizedPlanNode child in node.ChildPlans)
        {
            var res = FindSortSpaceUsedKb(child);
            if (res.HasValue)
            {
                return res.Value;
            }
        }
        return null;
    }

    public static string DetermineDominantBottleneck(
        List<QueryProfileResult> hybridQueries)
    {
        QueryProfileResult? sem = hybridQueries.FirstOrDefault(q => q.Role == "Semantic Candidates");
        QueryProfileResult? lex = hybridQueries.FirstOrDefault(q => q.Role == "Hybrid Lexical Candidates");

        if (sem != null && lex != null)
        {
            return $"Measured hybrid branch DB times: semantic={sem.DatabaseExecutionTimeMs:F1}ms, lexical={lex.DatabaseExecutionTimeMs:F1}ms. Causal attribution: unknown.";
        }

        return "Single candidate branch retrieval observed. Causal attribution: unknown.";
    }

    public static async Task<(int FilterEligible, int SemanticEligible)> CountEligibleAssetsAsync(
        SearchEvaluationDbFixture fixture,
        GetAssetsRequest request,
        string modelKey,
        CancellationToken cancellationToken)
    {
        await using ApplicationDbContext db = fixture.CreateDbContext();
        return await CountEligibleAssetsAsync(db, request, modelKey, cancellationToken);
    }

    public static async Task<(int FilterEligible, int SemanticEligible)> CountEligibleAssetsAsync(
        ApplicationDbContext db,
        GetAssetsRequest request,
        string modelKey,
        CancellationToken cancellationToken)
    {
        IQueryable<Asset> baseQuery = db.Assets.AsNoTracking()
            .Where(a => a.DeletedAt == null && a.Versions.Any(v => v.IsCurrent && v.ProcessingStatus == AssetVersionProcessingStatus.READY));

        if (request.CategoryId.HasValue)
        {
            Guid categoryId = request.CategoryId.Value;
            baseQuery = baseQuery.Where(a => a.CategoryId == categoryId);
        }

        if (request.AuthorId.HasValue)
        {
            Guid authorId = request.AuthorId.Value;
            baseQuery = baseQuery.Where(a => a.AuthorId == authorId);
        }

        if (request.MinPrice.HasValue)
        {
            var minPrice = request.MinPrice.Value;
            baseQuery = baseQuery.Where(a => a.Price >= minPrice);
        }

        if (request.MaxPrice.HasValue)
        {
            var maxPrice = request.MaxPrice.Value;
            baseQuery = baseQuery.Where(a => a.Price <= maxPrice);
        }

        if (request.Tags is { Count: > 0 })
        {
            foreach (var tag in request.Tags)
            {
                var tagName = tag;
                baseQuery = baseQuery.Where(a => a.AssetTags.Any(at => at.Tag.Name == tagName));
            }
        }

        var filterEligible = await baseQuery.CountAsync(cancellationToken);

        var semanticEligible = await baseQuery
            .Join(
                db.AssetEmbeddings.Where(e => e.ModelKey == modelKey),
                a => a.Id,
                e => e.AssetId,
                (a, e) => new { Asset = a, Embedding = e })
            .Where(x => x.Embedding.SourceRevision == x.Asset.SearchRevision)
            .Select(x => x.Asset.Id)
            .Distinct()
            .CountAsync(cancellationToken);

        return (filterEligible, semanticEligible);
    }

    public static void ValidateHybridHandlerExecution(
        Result<CatalogPageResult<AssetListItem>> result,
        SearchBenchmarkRunner.BenchmarkExecutionTracker tracker,
        SearchBenchmarkRunner.CachedVectorOnlyEmbeddingGenerator generator)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Hybrid handler execution failed with status: {result.Status}. Profiling evidence is invalid.");
        }

        if (tracker.HybridInvocations != 1 || tracker.LexicalInvocations != 0 || tracker.ProviderCalls != 0 || generator.InvocationCount != 0)
        {
            throw new InvalidOperationException(
                $"Hybrid handler execution invariant violated: expected 1 hybrid invocation, 0 lexical invocations, and 0 provider calls. Observed: hybrid={tracker.HybridInvocations}, lexical={tracker.LexicalInvocations}, providerCalls={tracker.ProviderCalls}, generatorInvocations={generator.InvocationCount}. Profiling evidence is invalid.");
        }
    }

    public static void ValidateLexicalFallbackHandlerExecution(
        Result<CatalogPageResult<AssetListItem>> result,
        SearchBenchmarkRunner.BenchmarkExecutionTracker tracker,
        SearchBenchmarkRunner.CachedVectorOnlyEmbeddingGenerator generator)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Lexical fallback handler execution failed with status: {result.Status}. Profiling evidence is invalid.");
        }

        if (tracker.LexicalInvocations != 1 || tracker.HybridInvocations != 0 || tracker.ProviderCalls != 0 || generator.InvocationCount != 0)
        {
            throw new InvalidOperationException(
                $"Lexical fallback handler invariant violated: expected 1 lexical invocation, 0 hybrid invocations, and 0 provider calls. Observed: lexical={tracker.LexicalInvocations}, hybrid={tracker.HybridInvocations}, providerCalls={tracker.ProviderCalls}, generatorInvocations={generator.InvocationCount}. Profiling evidence is invalid.");
        }
    }

    public static string SynthesizeBottleneckAttribution(List<ScenarioProfileResult> scenarios)
    {
        var semQueries = scenarios
            .SelectMany(s => s.HybridQueries.Where(q => q.Role == "Semantic Candidates"))
            .ToList();

        var lexQueries = scenarios
            .SelectMany(s => s.HybridQueries.Where(q => q.Role == "Hybrid Lexical Candidates"))
            .ToList();

        var semTimes = semQueries.Select(q => q.DatabaseExecutionTimeMs).ToList();
        var lexTimes = lexQueries.Select(q => q.DatabaseExecutionTimeMs).ToList();

        var semMean = semTimes.Count > 0 ? semTimes.Average() : 0.0;
        var lexMean = lexTimes.Count > 0 ? lexTimes.Average() : 0.0;

        // Dynamically extract observed plan node types and sort methods from observed sanitized plans
        var observedSemNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observedSemSorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (QueryProfileResult q in semQueries)
        {
            CollectPlanNodes(q.Plan, observedSemNodes, observedSemSorts);
        }

        var observedLexNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var observedLexSorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (QueryProfileResult q in lexQueries)
        {
            CollectPlanNodes(q.Plan, observedLexNodes, observedLexSorts);
        }

        var allSortMethods = observedSemSorts.Concat(observedLexSorts).Distinct().ToList();

        var hasDiskSpill = scenarios
            .SelectMany(s => s.HybridQueries.Concat(s.LexicalFallbackQueries))
            .Any(q => q.HasSortSpill);

        var totalTempBlocks = scenarios
            .SelectMany(s => s.HybridQueries.Concat(s.LexicalFallbackQueries))
            .Sum(q => q.TempBlocks);

        // Dynamically calculate handler vs store delta from measured samples
        var measuredDeltas = scenarios
            .Where(s => s.HybridHandlerLatency.SampleCount > 0 && s.HybridStoreLatency.SampleCount > 0)
            .Select(s => s.HybridHandlerLatency.P50Ms - s.HybridStoreLatency.P50Ms)
            .ToList();

        string handlerOverheadSummary;
        if (measuredDeltas.Count > 0)
        {
            var minDelta = measuredDeltas.Min();
            var maxDelta = measuredDeltas.Max();
            var meanDelta = measuredDeltas.Average();
            handlerOverheadSummary = $"In-process handler vs store descriptive p50 delta range: {minDelta:F2} ms to {maxDelta:F2} ms (mean: {meanDelta:F2} ms). In-process handler overhead attribution: unknown (measured deltas reflect separate sequential executions across distinct runs, not isolated in-process overhead). RRF candidate fusion occurs within store execution; handler performs cache check and model validation.";
        }
        else
        {
            handlerOverheadSummary = "In-process handler overhead: unknown (not measured in this diagnostic run).";
        }

        var semPlanDesc = observedSemNodes.Count > 0
            ? $"Observed semantic plan nodes: [{string.Join(", ", observedSemNodes.OrderBy(x => x))}]. Observed sort methods: [{(observedSemSorts.Count > 0 ? string.Join(", ", observedSemSorts.OrderBy(x => x)) : "none")}]."
            : "No semantic candidate plan nodes observed.";

        var lexPlanDesc = observedLexNodes.Count > 0
            ? $"Observed lexical plan nodes: [{string.Join(", ", observedLexNodes.OrderBy(x => x))}]."
            : "No lexical candidate plan nodes observed.";

        var sortDesc = hasDiskSpill
            ? $"Sort Space Analysis confirmed: DISK SPILL DETECTED ({totalTempBlocks} temp blocks used, disk sort space active)."
            : $"Sort Space Analysis confirmed: In-memory sorting observed across all evaluated queries (0 disk temp blocks; observed sort methods: {(allSortMethods.Count > 0 ? string.Join(", ", allSortMethods) : "none")}).";

        return $"""
            1. Observed Semantic Candidate Timings:
               Across evaluated scenarios, Semantic Candidate retrieval averaged {semMean:F1} ms in PostgreSQL execution time.
               {semPlanDesc}
               Causal attribution: unknown.
            2. Observed Lexical Candidate Timings:
               Hybrid Lexical Candidate retrieval averaged {lexMean:F1} ms in PostgreSQL execution time.
               {lexPlanDesc}
               Causal attribution: unknown.
            3. Sort & Memory Spill:
               {sortDesc}
            4. Execution Breakdown:
               {handlerOverheadSummary}
            5. Protocol Limits:
               Seven c1 diagnostic scenarios are exploratory only. Required next evidence: complete prescribed exact-search benchmark (24/24 cells). No ANN/HNSW recommendation from this diagnostic.
            """;
    }

    private static void CollectPlanNodes(SanitizedPlanNode node, HashSet<string> nodes, HashSet<string> sorts)
    {
        if (!string.IsNullOrWhiteSpace(node.NodeType))
        {
            nodes.Add(node.NodeType);
        }
        if (!string.IsNullOrWhiteSpace(node.SortMethod))
        {
            sorts.Add(node.SortMethod);
        }
        foreach (SanitizedPlanNode child in node.ChildPlans)
        {
            CollectPlanNodes(child, nodes, sorts);
        }
    }

    private static ScenarioLatencyMetrics ComputeLatencyMetrics(List<double> latencies)
    {
        if (latencies.Count == 0)
        {
            return new ScenarioLatencyMetrics(0, 0, 0, 0, 0, 0);
        }

        (var mean, var p50, var p95, var min, var max) = LocalOllamaEvaluator.CalculateLatencyStats(latencies);
        return new ScenarioLatencyMetrics(latencies.Count, mean, p50, p95, min, max);
    }

    private static async Task<Dictionary<string, int>> SeedCorpusAndFixturesAsync(
        SearchEvaluationDbFixture fixture,
        int totalAssets,
        Guid authorId,
        Guid secondaryAuthorId,
        List<Guid> categoryIds,
        List<Guid> tagIds,
        string modelKey,
        EmbeddingOptions embeddingOptions,
        CancellationToken cancellationToken)
    {
        const int batchSize = 2500;
        var remaining = totalAssets;
        var current = 0;

        var titles = new[]
        {
            "Fantasy Iron Sword and Shield Kit",
            "Sci-Fi Modular Plasma Rifle Asset",
            "Medieval Stone Wall and Fortress Kit",
            "Stylized Forest Vegetation Pack",
            "PBR Brick Cobblestone Texture Material",
            "Electronic Ambient Soundtrack Loop",
            "Character Controller Movement Script",
            "Particle Explosion Magic VFX",
            "Realistic Military Tank Vehicle Model",
            "Cartoon UI Icons and Button Pack"
        };

        _ = new Random(42);

        while (remaining > 0)
        {
            var currentBatch = Math.Min(remaining, batchSize);
            await using (ApplicationDbContext db = fixture.CreateDbContext())
            {
                db.ChangeTracker.AutoDetectChangesEnabled = false;

                var assets = new List<Asset>(currentBatch);
                var versions = new List<AssetVersion>(currentBatch);
                var embeddings = new List<AssetEmbedding>(currentBatch);
                var assetTags = new List<AssetTag>();

                for (var i = 0; i < currentBatch; i++)
                {
                    var idx = current + i;
                    var assetId = Guid.NewGuid();
                    Guid assignedAuthor = (idx % 7 == 0) ? secondaryAuthorId : authorId;
                    Guid categoryId = categoryIds[idx % categoryIds.Count];
                    var title = $"{titles[idx % titles.Length]} #{idx:D6}";
                    var description = $"High quality production asset {idx} optimized for game engines with modular components and clean textures.";
                    DateTimeOffset now = DateTimeOffset.UtcNow.AddMinutes(-idx);

                    var price = 10.00m + (idx % 20) * 2.00m; // range $10 - $48

                    assets.Add(new Asset
                    {
                        Id = assetId,
                        AuthorId = assignedAuthor,
                        CategoryId = categoryId,
                        Title = title,
                        Description = description,
                        Price = price,
                        CreatedAt = now,
                        UpdatedAt = now,
                        SearchRevision = 1L,
                        DeletedAt = null,
                        RatingAverage = 4.5,
                        RatingCount = 10
                    });

                    versions.Add(new AssetVersion
                    {
                        Id = Guid.NewGuid(),
                        AssetId = assetId,
                        VersionNumber = 1,
                        IsCurrent = true,
                        StorageKey = $"storage/asset-{idx}/v1.zip",
                        FileName = $"asset-{idx}.zip",
                        ContentLength = 1048576,
                        ContentSha256 = new string('a', 64),
                        ReleaseNotes = "Initial release",
                        LicenseCode = AssetLicenseCode.PERSONAL,
                        LicenseTemplateVersion = "1.0",
                        LicenseDisplayName = "Standard License",
                        LicenseTerms = "Standard terms",
                        ProcessingStatus = AssetVersionProcessingStatus.READY,
                        ProcessingUpdatedAt = now,
                        CreatedAt = now
                    });

                    var unitVector = GenerateDeterministicVector(title, embeddingOptions.Dimension);

                    embeddings.Add(new AssetEmbedding
                    {
                        Id = Guid.NewGuid(),
                        AssetId = assetId,
                        ModelKey = modelKey,
                        Provider = embeddingOptions.Provider,
                        ModelId = embeddingOptions.Model,
                        ModelRevision = embeddingOptions.Revision,
                        ModelDigest = embeddingOptions.Digest,
                        Dimension = embeddingOptions.Dimension,
                        ContentSchemaVersion = embeddingOptions.ContentSchemaVersion,
                        SourceRevision = 1L,
                        ContentHash = new string((char)('0' + (idx % 10)), 64),
                        Embedding = new Vector(unitVector),
                        CreatedAt = now,
                        UpdatedAt = now
                    });

                    // Assign tags
                    if (idx % 2 == 0)
                    {
                        assetTags.Add(new AssetTag { AssetId = assetId, TagId = tagIds[0] }); // medieval
                    }
                    if (idx % 3 == 0)
                    {
                        assetTags.Add(new AssetTag { AssetId = assetId, TagId = tagIds[1] }); // weapon
                    }
                }

                await db.Assets.AddRangeAsync(assets, cancellationToken);
                await db.AssetVersions.AddRangeAsync(versions, cancellationToken);
                await db.AssetEmbeddings.AddRangeAsync(embeddings, cancellationToken);
                await db.AssetTags.AddRangeAsync(assetTags, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            current += currentBatch;
            remaining -= currentBatch;
        }

        // Negative Fixtures:
        // 1. Soft-deleted assets
        // 2. Non-READY versions (e.g. PENDING_INSPECTION)
        // 3. Non-current versions
        // 4. Stale embedding source revision
        // 5. Mismatched embedding model key
        var negativeCounts = new Dictionary<string, int>();

        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            var negAssets = new List<Asset>();
            var negVersions = new List<AssetVersion>();
            var negEmbeddings = new List<AssetEmbedding>();

            // 1. Deleted assets (20)
            for (var i = 0; i < 20; i++)
            {
                var id = Guid.NewGuid();
                negAssets.Add(new Asset
                {
                    Id = id,
                    AuthorId = authorId,
                    CategoryId = categoryIds[0],
                    Title = $"Deleted Sword Fixture #{i}",
                    Description = "Deleted asset fixture",
                    Price = 19.99m,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    DeletedAt = DateTimeOffset.UtcNow,
                    SearchRevision = 1L
                });
                negVersions.Add(new AssetVersion
                {
                    Id = Guid.NewGuid(),
                    AssetId = id,
                    VersionNumber = 1,
                    IsCurrent = true,
                    StorageKey = $"neg-del-{i}.zip",
                    FileName = $"neg-del-{i}.zip",
                    ContentSha256 = new string('b', 64),
                    ContentLength = 1024,
                    ReleaseNotes = "Initial release",
                    LicenseCode = AssetLicenseCode.PERSONAL,
                    LicenseTemplateVersion = "1.0",
                    LicenseDisplayName = "Standard License",
                    LicenseTerms = "Standard terms",
                    ProcessingStatus = AssetVersionProcessingStatus.READY,
                    ProcessingUpdatedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            negativeCounts["Soft-Deleted Assets"] = 20;

            // 2. Pending/Processing versions (20)
            for (var i = 0; i < 20; i++)
            {
                var id = Guid.NewGuid();
                negAssets.Add(new Asset
                {
                    Id = id,
                    AuthorId = authorId,
                    CategoryId = categoryIds[0],
                    Title = $"Pending Inspection Shield Fixture #{i}",
                    Description = "Unready asset fixture",
                    Price = 19.99m,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    SearchRevision = 1L
                });
                negVersions.Add(new AssetVersion
                {
                    Id = Guid.NewGuid(),
                    AssetId = id,
                    VersionNumber = 1,
                    IsCurrent = false,
                    StorageKey = $"neg-pend-{i}.zip",
                    FileName = $"neg-pend-{i}.zip",
                    ContentSha256 = new string('c', 64),
                    ContentLength = 1024,
                    ReleaseNotes = "Initial release",
                    LicenseCode = AssetLicenseCode.PERSONAL,
                    LicenseTemplateVersion = "1.0",
                    LicenseDisplayName = "Standard License",
                    LicenseTerms = "Standard terms",
                    ProcessingStatus = AssetVersionProcessingStatus.PENDING_INSPECTION,
                    ProcessingUpdatedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            negativeCounts["Unready Processing Status Assets"] = 20;

            // 3. Stale revision embeddings (20)
            for (var i = 0; i < 20; i++)
            {
                var id = Guid.NewGuid();
                negAssets.Add(new Asset
                {
                    Id = id,
                    AuthorId = authorId,
                    CategoryId = categoryIds[0],
                    Title = $"Stale Revision Sword Fixture #{i}",
                    Description = "Stale embedding revision fixture",
                    Price = 19.99m,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    SearchRevision = 2L // Asset is on rev 2
                });
                negVersions.Add(new AssetVersion
                {
                    Id = Guid.NewGuid(),
                    AssetId = id,
                    VersionNumber = 1,
                    IsCurrent = true,
                    StorageKey = $"neg-stale-{i}.zip",
                    FileName = $"neg-stale-{i}.zip",
                    ContentSha256 = new string('d', 64),
                    ContentLength = 1024,
                    ReleaseNotes = "Initial release",
                    LicenseCode = AssetLicenseCode.PERSONAL,
                    LicenseTemplateVersion = "1.0",
                    LicenseDisplayName = "Standard License",
                    LicenseTerms = "Standard terms",
                    ProcessingStatus = AssetVersionProcessingStatus.READY,
                    ProcessingUpdatedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                negEmbeddings.Add(new AssetEmbedding
                {
                    Id = Guid.NewGuid(),
                    AssetId = id,
                    ModelKey = modelKey,
                    Provider = embeddingOptions.Provider,
                    ModelId = embeddingOptions.Model,
                    ModelRevision = embeddingOptions.Revision,
                    ModelDigest = embeddingOptions.Digest,
                    Dimension = embeddingOptions.Dimension,
                    ContentSchemaVersion = embeddingOptions.ContentSchemaVersion,
                    SourceRevision = 1L, // Stale: does not match SearchRevision 2
                    ContentHash = new string('0', 64),
                    Embedding = new Vector(GenerateDeterministicVector("stale", embeddingOptions.Dimension)),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            negativeCounts["Stale SourceRevision Embeddings"] = 20;

            // 4. Mismatched model embeddings (20)
            for (var i = 0; i < 20; i++)
            {
                var id = Guid.NewGuid();
                negAssets.Add(new Asset
                {
                    Id = id,
                    AuthorId = authorId,
                    CategoryId = categoryIds[0],
                    Title = $"Wrong Model Key Sword Fixture #{i}",
                    Description = "Wrong model key embedding fixture",
                    Price = 19.99m,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    SearchRevision = 1L
                });
                negVersions.Add(new AssetVersion
                {
                    Id = Guid.NewGuid(),
                    AssetId = id,
                    VersionNumber = 1,
                    IsCurrent = true,
                    StorageKey = $"neg-model-{i}.zip",
                    FileName = $"neg-model-{i}.zip",
                    ContentSha256 = new string('e', 64),
                    ContentLength = 1024,
                    ReleaseNotes = "Initial release",
                    LicenseCode = AssetLicenseCode.PERSONAL,
                    LicenseTemplateVersion = "1.0",
                    LicenseDisplayName = "Standard License",
                    LicenseTerms = "Standard terms",
                    ProcessingStatus = AssetVersionProcessingStatus.READY,
                    ProcessingUpdatedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                negEmbeddings.Add(new AssetEmbedding
                {
                    Id = Guid.NewGuid(),
                    AssetId = id,
                    ModelKey = new string('f', 64),
                    Provider = "Ollama",
                    ModelId = "legacy-model",
                    ModelRevision = "v1",
                    ModelDigest = "sha256:" + new string('0', 64),
                    Dimension = embeddingOptions.Dimension,
                    ContentSchemaVersion = "v1",
                    SourceRevision = 1L,
                    ContentHash = new string('0', 64),
                    Embedding = new Vector(GenerateDeterministicVector("wrong", embeddingOptions.Dimension)),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            negativeCounts["Mismatched ModelKey Embeddings"] = 20;

            await db.Assets.AddRangeAsync(negAssets, cancellationToken);
            await db.AssetVersions.AddRangeAsync(negVersions, cancellationToken);
            await db.AssetEmbeddings.AddRangeAsync(negEmbeddings, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return negativeCounts;
    }

    private static float[] GenerateDeterministicVector(string text, int dimension)
    {
        var seed = 0;
        foreach (var character in text)
        {
            seed = unchecked(seed * 31 + character);
        }

        var rng = new Random(seed);
        var vector = new float[dimension];
        var normSq = 0.0;
        for (var d = 0; d < dimension; d++)
        {
            var value = (float)(rng.NextDouble() * 2.0 - 1.0);
            vector[d] = value;
            normSq += value * value;
        }

        var norm = (float)Math.Sqrt(normSq);
        for (var d = 0; d < dimension; d++)
        {
            vector[d] /= norm;
        }

        return vector;
    }
}
