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
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using AssetBlock.SearchEvaluation.Evaluation;
using AssetBlock.SearchEvaluation.Infrastructure;
using AssetBlock.SearchEvaluation.Ollama;
using AssetBlock.SearchEvaluation.Reporting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pgvector;

namespace AssetBlock.SearchEvaluation.Benchmark;

#pragma warning disable CA5394 // Deterministic pseudo-randomness required for reproducible benchmark fixtures
public static class SearchBenchmarkRunner
{
    private const double TARGET_DB_HYBRID_P95_MS = 200.0;
    private const double TARGET_CACHED_FULL_PATH_P95_MS = 300.0;
    private const double TARGET_LEXICAL_FALLBACK_P95_MS = 200.0;

    private const int PRESCRIBED_WARMUP = 100;
    private const int PRESCRIBED_SAMPLES = 1000;
    private static readonly IReadOnlyList<int> _prescribedSizes = [1000, 10000, 50000];
    private static readonly IReadOnlyList<int> _prescribedConcurrency = [1, 10];

    public static bool IsPrescribedDecisionProtocol(
        List<int> corpusSizes,
        int warmupCount,
        int sampleCount,
        List<int> concurrencyLevels)
    {
        return warmupCount == PRESCRIBED_WARMUP
            && sampleCount == PRESCRIBED_SAMPLES
            && corpusSizes.Count == _prescribedSizes.Count
            && corpusSizes.SequenceEqual(_prescribedSizes)
            && concurrencyLevels.Count == _prescribedConcurrency.Count
            && concurrencyLevels.SequenceEqual(_prescribedConcurrency);
    }

    public static async Task<int> RunBenchmarkAsync(
        List<int> corpusSizes,
        int warmupCount,
        int sampleCount,
        List<int> concurrencyLevels,
        EmbeddingOptions embeddingOptions,
        IOllamaEmbeddingClient? ollamaClient,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("==========================================================");
        Console.WriteLine(" AssetBlock Exact-Search pgvector Benchmark");
        Console.WriteLine("==========================================================");
        Console.WriteLine($"Corpus Sizes:     {string.Join(", ", corpusSizes)}");
        Console.WriteLine($"Warm-up Queries:  {warmupCount}");
        Console.WriteLine($"Sample Queries:   {sampleCount}");
        Console.WriteLine($"Concurrency:      {string.Join(", ", concurrencyLevels)}");
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
        Console.WriteLine($"[PASS] Isolated PostgreSQL container initialized.");
        Console.WriteLine($"  PostgreSQL:     {provenance.PostgresVersion}");
        Console.WriteLine($"  pgvector:       {provenance.PgVectorVersion}");
        Console.WriteLine($"  Container:      {provenance.ContainerImageDigest}");
        Console.WriteLine($"  HNSW state:     {provenance.HnswState}");
        Console.ResetColor();
        Console.WriteLine();

        // 1. Seed base authors and categories once
        Guid authorId;
        var categoryIds = new List<Guid>();
        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            var author = new User
            {
                Id = Guid.NewGuid(),
                Username = "benchmark_author",
                Email = "benchmark_author@example.com",
                PasswordHash = "hash",
                Role = "SELLER",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Users.Add(author);
            authorId = author.Id;

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

            await db.SaveChangesAsync(cancellationToken);
        }

        // 2. Generate deterministic benchmark queries and query vectors
        List<(string Text, float[] Vector)> benchmarkQueries = GenerateBenchmarkQueries(count: Math.Max(warmupCount, sampleCount), embeddingOptions.Dimension);

        var corpusResults = new List<CorpusBenchmarkResult>();
        var currentCorpusSize = 0;

        foreach (var targetCorpusSize in corpusSizes.OrderBy(x => x))
        {
            var additionalNeeded = targetCorpusSize - currentCorpusSize;
            if (additionalNeeded > 0)
            {
                Console.WriteLine($"--> Seeding {additionalNeeded:N0} additional assets (Target total: {targetCorpusSize:N0})...");
                var seedStopwatch = Stopwatch.StartNew();
                await SeedAssetsAsync(fixture, currentCorpusSize, additionalNeeded, authorId, categoryIds, modelKey, embeddingOptions, cancellationToken);
                seedStopwatch.Stop();
                currentCorpusSize = targetCorpusSize;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[PASS] Corpus ready: {currentCorpusSize:N0} assets with current ready versions and vectors ({seedStopwatch.Elapsed.TotalSeconds:F1}s).");
                Console.ResetColor();
                Console.WriteLine();
            }

            // Warm-up phase
            Console.WriteLine($"--> Executing {warmupCount} warm-up queries for corpus size {targetCorpusSize:N0}...");
            await ExecuteWarmupAsync(fixture, benchmarkQueries.Take(warmupCount).ToList(), modelKey, cancellationToken);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[PASS] Warm-up complete.");
            Console.ResetColor();
            Console.WriteLine();

            var concurrencyResults = new List<ConcurrencyBenchmarkResult>();

            foreach (var concurrency in concurrencyLevels)
            {
                Console.WriteLine($"--> Measuring {sampleCount} samples at Concurrency {concurrency} (Corpus: {targetCorpusSize:N0})...");
                ConcurrencyBenchmarkResult result = await MeasureConcurrencyAsync(
                    fixture,
                    benchmarkQueries.Take(sampleCount).ToList(),
                    concurrency,
                    targetCorpusSize,
                    modelKey,
                    embeddingOptions,
                    ollamaClient,
                    cancellationToken);

                concurrencyResults.Add(result);
                PrintConcurrencyResult(result);
            }

            corpusResults.Add(new CorpusBenchmarkResult(
                targetCorpusSize,
                warmupCount,
                sampleCount,
                concurrencyResults));
        }

        // 3. Evaluate Prescribed Protocol & 50k Target Gates
        var isPrescribed = IsPrescribedDecisionProtocol(corpusSizes, warmupCount, sampleCount, concurrencyLevels);
        var exactScanPassed = false;
        string conclusion;

        if (!isPrescribed)
        {
            exactScanPassed = false;
            conclusion = $"Exploratory benchmark run completed with non-prescribed parameters (warmup={warmupCount}, samples={sampleCount}, sizes=[{string.Join(", ", corpusSizes)}], concurrency=[{string.Join(", ", concurrencyLevels)}]). Rollout / HNSW decision requires the exact prescribed protocol: warmup={PRESCRIBED_WARMUP}, samples={PRESCRIBED_SAMPLES}, sizes=[{string.Join(", ", _prescribedSizes)}], concurrency=[{string.Join(", ", _prescribedConcurrency)}]. No exact/HNSW rollout verdict permitted.";
        }
        else
        {
            CorpusBenchmarkResult? corpus50K = corpusResults.FirstOrDefault(c => c.CorpusSize == 50000);
            if (corpus50K == null)
            {
                conclusion = "Corpus size 50,000 was not evaluated; cannot declare exact scan sufficiency.";
            }
            else
            {
                ConcurrencyBenchmarkResult? c1 = corpus50K.ConcurrencyResults.FirstOrDefault(c => c.Concurrency == 1);
                ConcurrencyBenchmarkResult? c10 = corpus50K.ConcurrencyResults.FirstOrDefault(c => c.Concurrency == 10);

                PathBenchmarkMetrics? hybrid1 = c1?.PathMetrics.FirstOrDefault(p => p.PathName == "DB Hybrid Retrieval");
                PathBenchmarkMetrics? fullPath1 = c1?.PathMetrics.FirstOrDefault(p => p.PathName == "Full Catalog (Cached Vector)");
                PathBenchmarkMetrics? lexical1 = c1?.PathMetrics.FirstOrDefault(p => p.PathName == "Lexical Fallback");
                PathBenchmarkMetrics? ollama1 = c1?.PathMetrics.FirstOrDefault(p => p.PathName == "Uncached Ollama Query Generation");
                PathBenchmarkMetrics? ollama10 = c10?.PathMetrics.FirstOrDefault(p => p.PathName == "Uncached Ollama Query Generation");

                var passesDbHybrid = hybrid1 is { P95Ms: <= TARGET_DB_HYBRID_P95_MS };
                var passesFullPath = fullPath1 is { P95Ms: <= TARGET_CACHED_FULL_PATH_P95_MS };
                var passesLexical = lexical1 is { P95Ms: <= TARGET_LEXICAL_FALLBACK_P95_MS };
                var hasCompleteOllama1 = ollama1 != null && ollama1.SampleCount == sampleCount && ollama1.SampleCount > 0;
                var hasCompleteOllama10 = ollama10 != null && ollama10.SampleCount == sampleCount && ollama10.SampleCount > 0;

                if (!hasCompleteOllama1 || !hasCompleteOllama10)
                {
                    exactScanPassed = false;
                    conclusion = $"Cannot declare exact scan sufficiency: uncached Ollama generation evidence is incomplete (Concurrency 1: {ollama1?.SampleCount ?? 0}/{sampleCount}; Concurrency 10: {ollama10?.SampleCount ?? 0}/{sampleCount}). Ollama must be running and measured at all configured samples and concurrency levels.";
                }
                else if (passesDbHybrid && passesFullPath && passesLexical)
                {
                    exactScanPassed = true;
                    conclusion = $"Exact scan PASSED target gates at 50,000 assets (DB Hybrid p95: {hybrid1?.P95Ms:F1} ms <= {TARGET_DB_HYBRID_P95_MS:F0} ms; Full Catalog p95: {fullPath1?.P95Ms:F1} ms <= {TARGET_CACHED_FULL_PATH_P95_MS:F0} ms; Lexical Fallback p95: {lexical1?.P95Ms:F1} ms <= {TARGET_LEXICAL_FALLBACK_P95_MS:F0} ms; Ollama c1 p95: {ollama1?.P95Ms:F1} ms, c10 p95: {ollama10?.P95Ms:F1} ms). Exact scan is sufficient; HNSW index is not currently required.";
                }
                else
                {
                    exactScanPassed = false;
                    conclusion = $"Exact scan FAILED target gates at 50,000 assets (DB Hybrid p95: {hybrid1?.P95Ms:F1} ms, Full Catalog p95: {fullPath1?.P95Ms:F1} ms, Lexical Fallback p95: {lexical1?.P95Ms:F1} ms). HNSW evaluation is required. Do not implement HNSW, create an index, or generate a migration in this batch.";
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("==========================================================");
        Console.WriteLine(" Exact-Search Decision");
        Console.WriteLine("==========================================================");
        if (!isPrescribed)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[EXPLORATORY RUN - NO ROLLOUT DECISION PERMITTED]");
        }
        else if (exactScanPassed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[PASS] EXACT SCAN IS SUFFICIENT FOR 50,000 ASSETS");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[NOTICE] TARGETS NOT MET: HNSW EVALUATION REQUIRED");
        }
        Console.ResetColor();
        Console.WriteLine(conclusion);
        Console.WriteLine("==========================================================");

        var reportData = new BenchmarkReportData(
            provenance,
            embeddingOptions,
            corpusResults,
            exactScanPassed,
            conclusion,
            isPrescribed);

        (var jsonPath, var mdPath) = SearchEvaluationReportWriter.WriteBenchmarkReport(reportData);
        Console.WriteLine();
        Console.WriteLine($"Reports emitted:");
        Console.WriteLine($"  - JSON:     {jsonPath}");
        Console.WriteLine($"  - Markdown: {mdPath}");

        return exactScanPassed ? Program.EXIT_SUCCESS : Program.EXIT_MANUAL_EVALUATION_REQUIRED;
    }

    private static async Task SeedAssetsAsync(
        SearchEvaluationDbFixture fixture,
        int startIndex,
        int count,
        Guid authorId,
        List<Guid> categoryIds,
        string modelKey,
        EmbeddingOptions embeddingOptions,
        CancellationToken cancellationToken)
    {
        const int batchSize = 2500;
        var remaining = count;
        var current = startIndex;

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

        var rng = new Random(42 + startIndex);

        while (remaining > 0)
        {
            var currentBatch = Math.Min(remaining, batchSize);
            await using (ApplicationDbContext db = fixture.CreateDbContext())
            {
                db.ChangeTracker.AutoDetectChangesEnabled = false;

                var assets = new List<Asset>(currentBatch);
                var versions = new List<AssetVersion>(currentBatch);
                var embeddings = new List<AssetEmbedding>(currentBatch);

                for (var i = 0; i < currentBatch; i++)
                {
                    var idx = current + i;
                    var assetId = Guid.NewGuid();
                    Guid categoryId = categoryIds[idx % categoryIds.Count];
                    var titleTemplate = titles[idx % titles.Length];
                    var title = $"{titleTemplate} #{idx:D6}";
                    var description = $"High quality production asset {idx} optimized for game engines with modular components and clean textures.";

                    DateTimeOffset now = DateTimeOffset.UtcNow.AddMinutes(-idx);

                    assets.Add(new Asset
                    {
                        Id = assetId,
                        AuthorId = authorId,
                        CategoryId = categoryId,
                        Title = title,
                        Description = description,
                        Price = 19.99m,
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

                    // Generate deterministic unit vector
                    var rawVector = new float[embeddingOptions.Dimension];
                    var normSq = 0.0;
                    for (var d = 0; d < embeddingOptions.Dimension; d++)
                    {
                        var val = (float)(rng.NextDouble() * 2.0 - 1.0);
                        rawVector[d] = val;
                        normSq += val * val;
                    }
                    var norm = (float)Math.Sqrt(normSq);
                    for (var d = 0; d < embeddingOptions.Dimension; d++)
                    {
                        rawVector[d] /= norm;
                    }

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
                        Embedding = new Vector(rawVector),
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                }

                await db.Assets.AddRangeAsync(assets, cancellationToken);
                await db.AssetVersions.AddRangeAsync(versions, cancellationToken);
                await db.AssetEmbeddings.AddRangeAsync(embeddings, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            current += currentBatch;
            remaining -= currentBatch;
        }
    }

    private static async Task ExecuteWarmupAsync(
        SearchEvaluationDbFixture fixture,
        List<(string Text, float[] Vector)> queries,
        string modelKey,
        CancellationToken cancellationToken)
    {
        await using ApplicationDbContext db = fixture.CreateDbContext();
        var store = new AssetStore(db);

        foreach ((var text, var vector) in queries)
        {
            var req = new GetAssetsRequest { Search = text, Page = 1, PageSize = 20 };
            await store.GetPaged(req, vector, modelKey, cancellationToken);
            await store.GetPaged(req, null, null, cancellationToken);
        }
    }

    private static async Task<ConcurrencyBenchmarkResult> MeasureConcurrencyAsync(
        SearchEvaluationDbFixture fixture,
        List<(string Text, float[] Vector)> queries,
        int concurrency,
        int corpusSize,
        string modelKey,
        EmbeddingOptions embeddingOptions,
        IOllamaEmbeddingClient? ollamaClient,
        CancellationToken cancellationToken)
    {
        var dbHybridLatencies = new List<double>(queries.Count);
        var cachedFullPathLatencies = new List<double>(queries.Count);
        var lexicalLatencies = new List<double>(queries.Count);

        // Real bounded local caches
        var queryVectorCache = new BoundedQueryVectorCache(TimeProvider.System, maxEntries: Math.Max(queries.Count * 2, 1000));
        var memoryCacheService = new MemoryCacheService();
        var typedCache = new JsonTypedCache(memoryCacheService, NullLogger<JsonTypedCache>.Instance);
        var capability = new BenchmarkVectorSearchCapability(modelKey, isAvailable: true);

        // Pre-warm query vector cache with normalized hashes
        foreach ((var text, var vector) in queries)
        {
            var normalized = CatalogSearchNormalization.NormalizeSearchQuery(text);
            var searchHash = CacheKeys.HashSearchQuery(normalized);
            queryVectorCache.Set(modelKey, searchHash, vector, TimeSpan.FromHours(1));
        }

        if (concurrency == 1)
        {
            await using ApplicationDbContext db = fixture.CreateDbContext();
            var store = new AssetStore(db);
            var sw = new Stopwatch();

            // 1. DB Hybrid Retrieval
            foreach ((var text, var vector) in queries)
            {
                var req = new GetAssetsRequest { Search = text, Page = 1, PageSize = 20 };
                sw.Restart();
                await store.GetPaged(req, vector, modelKey, cancellationToken);
                sw.Stop();
                dbHybridLatencies.Add(sw.Elapsed.TotalMilliseconds);
            }

            // 2. Full Catalog Path (Cached Vector via GetAssetsQueryHandler)
            var handler = new GetAssetsQueryHandler(
                store,
                typedCache,
                queryVectorCache,
                capability,
                embeddingGenerator: null,
                Options.Create(embeddingOptions),
                NullLogger<GetAssetsQueryHandler>.Instance);

            foreach ((var text, _) in queries)
            {
                var req = new GetAssetsRequest { Search = text, Page = 1, PageSize = 20 };
                sw.Restart();
                Result<CatalogPageResult<AssetListItem>> result = await handler.Handle(new GetAssetsQuery(req), cancellationToken);
                sw.Stop();
                if (result.IsSuccess)
                {
                    cachedFullPathLatencies.Add(sw.Elapsed.TotalMilliseconds);
                }
            }

            // 3. Lexical Fallback
            foreach ((var text, _) in queries)
            {
                var req = new GetAssetsRequest { Search = text, Page = 1, PageSize = 20 };
                sw.Restart();
                await store.GetPaged(req, null, null, cancellationToken);
                sw.Stop();
                lexicalLatencies.Add(sw.Elapsed.TotalMilliseconds);
            }
        }
        else
        {
            // Concurrent execution using isolated DbContext instances
            var dbHybridBag = new System.Collections.Concurrent.ConcurrentBag<double>();
            var cachedPathBag = new System.Collections.Concurrent.ConcurrentBag<double>();
            var lexicalBag = new System.Collections.Concurrent.ConcurrentBag<double>();

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = cancellationToken
            };

            // 1. Concurrent DB Hybrid
            await Parallel.ForEachAsync(queries, parallelOptions, async (q, ct) =>
            {
                await using ApplicationDbContext db = fixture.CreateDbContext();
                var store = new AssetStore(db);
                var req = new GetAssetsRequest { Search = q.Text, Page = 1, PageSize = 20 };

                var sw = Stopwatch.StartNew();
                await store.GetPaged(req, q.Vector, modelKey, ct);
                sw.Stop();

                dbHybridBag.Add(sw.Elapsed.TotalMilliseconds);
            });

            // 2. Concurrent Full Catalog (Cached Vector via GetAssetsQueryHandler)
            await Parallel.ForEachAsync(queries, parallelOptions, async (q, ct) =>
            {
                await using ApplicationDbContext db = fixture.CreateDbContext();
                var store = new AssetStore(db);
                var handler = new GetAssetsQueryHandler(
                    store,
                    typedCache,
                    queryVectorCache,
                    capability,
                    embeddingGenerator: null,
                    Options.Create(embeddingOptions),
                    NullLogger<GetAssetsQueryHandler>.Instance);
                var req = new GetAssetsRequest { Search = q.Text, Page = 1, PageSize = 20 };

                var sw = Stopwatch.StartNew();
                Result<CatalogPageResult<AssetListItem>> result = await handler.Handle(new GetAssetsQuery(req), ct);
                sw.Stop();

                if (result.IsSuccess)
                {
                    cachedPathBag.Add(sw.Elapsed.TotalMilliseconds);
                }
            });

            // 3. Concurrent Lexical Fallback
            await Parallel.ForEachAsync(queries, parallelOptions, async (q, ct) =>
            {
                await using ApplicationDbContext db = fixture.CreateDbContext();
                var store = new AssetStore(db);
                var req = new GetAssetsRequest { Search = q.Text, Page = 1, PageSize = 20 };

                var sw = Stopwatch.StartNew();
                await store.GetPaged(req, null, null, ct);
                sw.Stop();

                lexicalBag.Add(sw.Elapsed.TotalMilliseconds);
            });

            dbHybridLatencies.AddRange(dbHybridBag);
            cachedFullPathLatencies.AddRange(cachedPathBag);
            lexicalLatencies.AddRange(lexicalBag);
        }

        // 4. Uncached Local-Ollama Query Generation (configured samples at current concurrency)
        var ollamaLatencies = new List<double>();
        if (ollamaClient != null)
        {
            if (concurrency == 1)
            {
                var sw = new Stopwatch();
                foreach ((var text, _) in queries)
                {
                    try
                    {
                        sw.Restart();
                        await ollamaClient.GenerateEmbedding(text, cancellationToken);
                        sw.Stop();
                        ollamaLatencies.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    catch
                    {
                        // Stop on failure but retain measured samples to demonstrate incomplete run
                        break;
                    }
                }
            }
            else
            {
                var ollamaBag = new System.Collections.Concurrent.ConcurrentBag<double>();
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = concurrency,
                    CancellationToken = cancellationToken
                };

                try
                {
                    await Parallel.ForEachAsync(queries, parallelOptions, async (q, ct) =>
                    {
                        var sw = Stopwatch.StartNew();
                        await ollamaClient.GenerateEmbedding(q.Text, ct);
                        sw.Stop();
                        ollamaBag.Add(sw.Elapsed.TotalMilliseconds);
                    });
                    ollamaLatencies.AddRange(ollamaBag);
                }
                catch
                {
                    ollamaLatencies.AddRange(ollamaBag);
                }
            }
        }

        var pathMetrics = new List<PathBenchmarkMetrics>
        {
            CreateMetrics("DB Hybrid Retrieval", dbHybridLatencies, queries.Count, corpusSize == 50000 ? TARGET_DB_HYBRID_P95_MS : null),
            CreateMetrics("Full Catalog (Cached Vector)", cachedFullPathLatencies, queries.Count, corpusSize == 50000 ? TARGET_CACHED_FULL_PATH_P95_MS : null),
            CreateMetrics("Lexical Fallback", lexicalLatencies, queries.Count, corpusSize == 50000 ? TARGET_LEXICAL_FALLBACK_P95_MS : null),
            CreateMetrics("Uncached Ollama Query Generation", ollamaLatencies, queries.Count, null)
        };

        return new ConcurrencyBenchmarkResult(concurrency, pathMetrics);
    }

    private static PathBenchmarkMetrics CreateMetrics(string pathName, List<double> latencies, int expectedCount, double? targetP95)
    {
        if (latencies.Count == 0)
        {
            return new PathBenchmarkMetrics(pathName, 0, 0, 0, 0, 0, 0, targetP95, false);
        }

        (var mean, var p50, var p95, var min, var max) = LocalOllamaEvaluator.CalculateLatencyStats(latencies);
        bool? passed;
        if (pathName.Contains("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            passed = latencies.Count == expectedCount && latencies.Count > 0;
        }
        else
        {
            passed = targetP95.HasValue ? p95 <= targetP95.Value : null;
        }

        return new PathBenchmarkMetrics(
            pathName,
            latencies.Count,
            mean,
            p50,
            p95,
            min,
            max,
            targetP95,
            passed);
    }

    private static void PrintConcurrencyResult(ConcurrencyBenchmarkResult result)
    {
        Console.WriteLine($"  Results for Concurrency {result.Concurrency}:");
        foreach (PathBenchmarkMetrics p in result.PathMetrics)
        {
            var targetStr = p.TargetP95Ms.HasValue ? $" | Target p95: <= {p.TargetP95Ms.Value:F0} ms [{(p.TargetPassed == true ? "PASS" : "FAIL")}]" : "";
            Console.WriteLine($"    - {p.PathName,-32} N={p.SampleCount,-4} | Mean: {p.MeanMs,6:F1} ms | p50: {p.P50Ms,6:F1} ms | p95: {p.P95Ms,6:F1} ms | Min: {p.MinMs,6:F1} ms | Max: {p.MaxMs,6:F1} ms{targetStr}");
        }
        Console.WriteLine();
    }

    private static List<(string Text, float[] Vector)> GenerateBenchmarkQueries(int count, int dimension)
    {
        var terms = new[]
        {
            "sword", "shield", "rifle", "castle", "forest", "texture", "audio", "script",
            "vfx", "magic", "tank", "space", "modular", "medieval", "armor", "particle",
            "terrain", "pbr", "stylized", "dungeon", "lowpoly", "lighting", "zombie", "vehicle"
        };

        var rng = new Random(1337);
        var result = new List<(string Text, float[] Vector)>(count);

        for (var i = 0; i < count; i++)
        {
            var t1 = terms[rng.Next(terms.Length)];
            var t2 = terms[rng.Next(terms.Length)];
            var text = $"{t1} {t2}";

            var vector = new float[dimension];
            var normSq = 0.0;
            for (var d = 0; d < dimension; d++)
            {
                var val = (float)(rng.NextDouble() * 2.0 - 1.0);
                vector[d] = val;
                normSq += val * val;
            }
            var norm = (float)Math.Sqrt(normSq);
            for (var d = 0; d < dimension; d++)
            {
                vector[d] /= norm;
            }

            result.Add((text, vector));
        }

        return result;
    }
}
