using System.Diagnostics;
using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.UseCases.Assets.GetAssets;
using AssetBlock.Domain.Abstractions.Services;
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
using DomainGeneratedEmbedding = AssetBlock.Domain.Abstractions.Services.GeneratedEmbedding;
using DomainModelVerificationResult = AssetBlock.Domain.Abstractions.Services.ModelVerificationResult;
using ITextEmbeddingGenerator = AssetBlock.Domain.Abstractions.Services.ITextEmbeddingGenerator;

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
        bool skipOllama = false,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("==========================================================");
        Console.WriteLine(" AssetBlock Exact-Search pgvector Benchmark");
        Console.WriteLine("==========================================================");
        Console.WriteLine($"Corpus Sizes:     {string.Join(", ", corpusSizes)}");
        Console.WriteLine($"Warm-up Queries:  {warmupCount}");
        Console.WriteLine($"Sample Queries:   {sampleCount}");
        Console.WriteLine($"Concurrency:      {string.Join(", ", concurrencyLevels)}");
        Console.WriteLine($"Skip Ollama:      {skipOllama}");
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
        Console.WriteLine($"  Fingerprint:    {provenance.SourceFingerprint ?? provenance.GitCommit}");
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
        var tracker = new BenchmarkExecutionTracker();

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
                    tracker,
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

        // 3. Evaluate Prescribed Protocol, Completeness, & 50k Performance Gates
        (BenchmarkProtocolConformance protocol, BenchmarkEvidenceCompleteness completeness, BenchmarkPerformanceDecision decision, var exactScanPassed, var conclusion) =
            EvaluateBenchmarkDecision(corpusSizes, warmupCount, sampleCount, concurrencyLevels, skipOllama, corpusResults, tracker);

        Console.WriteLine();
        Console.WriteLine("==========================================================");
        Console.WriteLine(" Exact-Search Protocol, Completeness & Performance Decision");
        Console.WriteLine("==========================================================");
        Console.WriteLine($"Protocol:     {(protocol.IsPrescribed ? "[PRESCRIBED]" : "[NON-PRESCRIBED / EXPLORATORY]")}");
        Console.WriteLine($"Completeness: {(completeness.IsComplete ? "[COMPLETE]" : "[INCOMPLETE]")}");
        if (!completeness.IsComplete)
        {
            foreach (var reason in completeness.IncompletenessReasons)
            {
                Console.WriteLine($"  - Incomplete Reason: {reason}");
            }
        }
        Console.WriteLine($"50k Targets:  {(decision.Passed50KTargets ? "[MET]" : "[NOT MET]")}");
        foreach (var detail in decision.TargetDetails)
        {
            Console.WriteLine($"  - {detail}");
        }
        Console.WriteLine($"Verdict:      {decision.RolloutVerdict}");
        Console.WriteLine($"HNSW Needed:  {(decision.HnswRecommended ? "YES (DB Hybrid bottleneck at 50k)" : "NO")}");
        Console.WriteLine($"Note:         {decision.QualityGateRequirementNote}");
        Console.WriteLine("==========================================================");
        Console.WriteLine(conclusion);
        Console.WriteLine("==========================================================");

        var reportData = new BenchmarkReportData(
            provenance,
            embeddingOptions,
            corpusResults,
            exactScanPassed,
            conclusion,
            protocol.IsPrescribed,
            protocol,
            completeness,
            decision);

        (var jsonPath, var mdPath) = SearchEvaluationReportWriter.WriteBenchmarkReport(reportData);
        Console.WriteLine();
        Console.WriteLine("Reports emitted:");
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
        BenchmarkExecutionTracker? tracker,
        CancellationToken cancellationToken)
    {
        var dbHybridLatencies = new List<double>(queries.Count);
        var cachedFullPathLatencies = new List<double>(queries.Count);
        var lexicalLatencies = new List<double>(queries.Count);

        // Query-vector cache is deliberately warm. The catalog-result cache is bypassed so this path measures retrieval.
        var queryVectorCache = new BoundedQueryVectorCache(TimeProvider.System, maxEntries: Math.Max(queries.Count * 2, 1000));
        ITypedCache resultCache = new ObservableResultCache(tracker);
        var cachedVectorGenerator = new CachedVectorOnlyEmbeddingGenerator(tracker);
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
            var observingStore = new EvaluationObservingAssetStore(store, tracker);
            var handler = new GetAssetsQueryHandler(
                observingStore,
                resultCache,
                queryVectorCache,
                capability,
                cachedVectorGenerator,
                Options.Create(embeddingOptions),
                NullLogger<GetAssetsQueryHandler>.Instance);

            foreach ((var text, _) in queries)
            {
                var req = new GetAssetsRequest { Search = text, Page = 1, PageSize = 20 };
                var preLexical = tracker?.LexicalInvocations ?? 0;
                var preHybrid = tracker?.HybridInvocations ?? 0;

                sw.Restart();
                Result<CatalogPageResult<AssetListItem>> result = await handler.Handle(new GetAssetsQuery(req), cancellationToken);
                sw.Stop();

                // Do not drop failures from denominator
                cachedFullPathLatencies.Add(sw.Elapsed.TotalMilliseconds);

                if (!result.IsSuccess)
                {
                    tracker?.RecordFailedSample();
                }
                if (tracker != null && tracker.LexicalInvocations > preLexical)
                {
                    // Hidden lexical fallback occurred
                }
                if (tracker != null && tracker.HybridInvocations == preHybrid)
                {
                    tracker.RecordFailedSample();
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
                var observingStore = new EvaluationObservingAssetStore(store, tracker);
                var handler = new GetAssetsQueryHandler(
                    observingStore,
                    resultCache,
                    queryVectorCache,
                    capability,
                    cachedVectorGenerator,
                    Options.Create(embeddingOptions),
                    NullLogger<GetAssetsQueryHandler>.Instance);
                var req = new GetAssetsRequest { Search = q.Text, Page = 1, PageSize = 20 };

                var preLexical = tracker?.LexicalInvocations ?? 0;
                var preHybrid = tracker?.HybridInvocations ?? 0;

                var sw = Stopwatch.StartNew();
                Result<CatalogPageResult<AssetListItem>> result = await handler.Handle(new GetAssetsQuery(req), ct);
                sw.Stop();

                // Do not drop failures from denominator
                cachedPathBag.Add(sw.Elapsed.TotalMilliseconds);

                if (!result.IsSuccess)
                {
                    tracker?.RecordFailedSample();
                }
                if (tracker != null && tracker.LexicalInvocations > preLexical)
                {
                    // Hidden lexical fallback occurred
                }
                if (tracker != null && tracker.HybridInvocations == preHybrid)
                {
                    tracker.RecordFailedSample();
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

        if (cachedVectorGenerator.InvocationCount != 0)
        {
            tracker?.RecordProviderCall();
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
            CreateMetrics("Full Catalog (Cached Query Vector)", cachedFullPathLatencies, queries.Count, corpusSize == 50000 ? TARGET_CACHED_FULL_PATH_P95_MS : null),
            CreateMetrics("Lexical Fallback", lexicalLatencies, queries.Count, corpusSize == 50000 ? TARGET_LEXICAL_FALLBACK_P95_MS : null),
            CreateMetrics("Uncached Ollama Query Generation", ollamaLatencies, queries.Count, null)
        };

        return new ConcurrencyBenchmarkResult(concurrency, pathMetrics);
    }

    public static (BenchmarkProtocolConformance Protocol, BenchmarkEvidenceCompleteness Completeness, BenchmarkPerformanceDecision Decision, bool ExactScanPassed, string Conclusion) EvaluateBenchmarkDecision(
        IReadOnlyList<int> corpusSizes,
        int warmupCount,
        int sampleCount,
        IReadOnlyList<int> concurrencyLevels,
        bool skipOllama,
        List<CorpusBenchmarkResult> corpusResults,
        BenchmarkExecutionTracker? tracker = null)
    {
        var isPrescribed = IsPrescribedDecisionProtocol(corpusSizes.ToList(), warmupCount, sampleCount, concurrencyLevels.ToList());
        var protocolSummary = isPrescribed
            ? "Run matches the exact prescribed protocol: sizes=[1000, 10000, 50000], warmup=100, samples=1000, concurrency=[1, 10]."
            : $"Run parameters deviate from prescribed protocol (sizes=[{string.Join(", ", corpusSizes)}], warmup={warmupCount}, samples={sampleCount}, concurrency=[{string.Join(", ", concurrencyLevels)}]). Exploratory diagnostics only.";
        var protocol = new BenchmarkProtocolConformance(
            isPrescribed,
            _prescribedSizes,
            corpusSizes,
            PRESCRIBED_WARMUP,
            warmupCount,
            PRESCRIBED_SAMPLES,
            sampleCount,
            _prescribedConcurrency,
            concurrencyLevels,
            protocolSummary);

        var reasons = new List<string>();
        var expectedCells = corpusSizes.Count * concurrencyLevels.Count * 4;
        var measuredCells = 0;
        var missingOrFailedSamples = 0;

        if (skipOllama)
        {
            reasons.Add("Explicit --skip-ollama was specified; uncached Ollama query generation was not evaluated.");
        }

        foreach (var size in corpusSizes)
        {
            CorpusBenchmarkResult? cResult = corpusResults.FirstOrDefault(c => c.CorpusSize == size);
            if (cResult == null)
            {
                reasons.Add($"Corpus size {size} is missing from benchmark results.");
                continue;
            }

            foreach (var conc in concurrencyLevels)
            {
                ConcurrencyBenchmarkResult? concResult = cResult.ConcurrencyResults.FirstOrDefault(c => c.Concurrency == conc);
                if (concResult == null)
                {
                    reasons.Add($"Concurrency {conc} for corpus size {size} is missing.");
                    continue;
                }

                foreach (var pathName in new[] { "DB Hybrid Retrieval", "Full Catalog (Cached Query Vector)", "Lexical Fallback" })
                {
                    PathBenchmarkMetrics? m = concResult.PathMetrics.FirstOrDefault(p => p.PathName == pathName);
                    if (m == null || m.SampleCount < sampleCount)
                    {
                        var actual = m?.SampleCount ?? 0;
                        missingOrFailedSamples += (sampleCount - actual);
                        reasons.Add($"Path '{pathName}' at size {size}, concurrency {conc} has incomplete samples ({actual}/{sampleCount}).");
                    }
                    else
                    {
                        measuredCells++;
                    }
                }

                PathBenchmarkMetrics? ollama = concResult.PathMetrics.FirstOrDefault(p => p.PathName == "Uncached Ollama Query Generation");
                if (ollama == null || ollama.SampleCount < sampleCount)
                {
                    var actual = ollama?.SampleCount ?? 0;
                    missingOrFailedSamples += (sampleCount - actual);
                    if (!skipOllama)
                    {
                        reasons.Add($"Uncached Ollama at size {size}, concurrency {conc} has incomplete samples ({actual}/{sampleCount}).");
                    }
                }
                else
                {
                    measuredCells++;
                }
            }
        }

        var hiddenFallbacks = tracker?.LexicalInvocations ?? 0;
        var providerCalls = tracker?.ProviderCalls ?? 0;
        var cacheHits = tracker?.ResultCacheHits ?? 0;
        var failedSamples = tracker?.FailedSamples ?? 0;
        missingOrFailedSamples += failedSamples;

        if (hiddenFallbacks > 0)
        {
            reasons.Add($"Hidden lexical fallback detected: {hiddenFallbacks} cached-vector queries fell back to lexical search.");
        }
        if (providerCalls > 0)
        {
            reasons.Add($"Provider calls detected in cached-vector benchmark: {providerCalls} calls to embedding generator.");
        }
        if (cacheHits > 0)
        {
            reasons.Add($"Catalog-result cache hit detected: {cacheHits} queries reused catalog cache instead of evaluating retrieval.");
        }

        var isComplete = reasons.Count == 0 && missingOrFailedSamples == 0;
        var completeness = new BenchmarkEvidenceCompleteness(
            isComplete,
            skipOllama,
            expectedCells,
            measuredCells,
            missingOrFailedSamples,
            hiddenFallbacks,
            providerCalls,
            cacheHits,
            reasons);

        var targetDetails = new List<string>();
        bool exactScanPassed;
        bool hnswRecommended;
        string rolloutVerdict;
        string conclusion;

        const string qualityGateNote = "Performance pass confirms retrieval latency targets only and does not constitute release-quality or rollout approval. Release-quality sign-off strictly requires release quality evaluation with independent human-adjudicated relevance judgments (qrels).";

        CorpusBenchmarkResult? corpus50K = corpusResults.FirstOrDefault(c => c.CorpusSize == 50000);
        ConcurrencyBenchmarkResult? c1 = corpus50K?.ConcurrencyResults.FirstOrDefault(c => c.Concurrency == 1);
        ConcurrencyBenchmarkResult? c10 = corpus50K?.ConcurrencyResults.FirstOrDefault(c => c.Concurrency == 10);

        PathBenchmarkMetrics? hybrid1 = c1?.PathMetrics.FirstOrDefault(p => p.PathName == "DB Hybrid Retrieval");
        PathBenchmarkMetrics? hybrid10 = c10?.PathMetrics.FirstOrDefault(p => p.PathName == "DB Hybrid Retrieval");

        PathBenchmarkMetrics? fullPath1 = c1?.PathMetrics.FirstOrDefault(p => p.PathName == "Full Catalog (Cached Query Vector)");
        PathBenchmarkMetrics? fullPath10 = c10?.PathMetrics.FirstOrDefault(p => p.PathName == "Full Catalog (Cached Query Vector)");

        PathBenchmarkMetrics? lexical1 = c1?.PathMetrics.FirstOrDefault(p => p.PathName == "Lexical Fallback");
        PathBenchmarkMetrics? lexical10 = c10?.PathMetrics.FirstOrDefault(p => p.PathName == "Lexical Fallback");

        var dbHybridPass1 = hybrid1 is { P95Ms: <= TARGET_DB_HYBRID_P95_MS };
        var dbHybridPass10 = hybrid10 is { P95Ms: <= TARGET_DB_HYBRID_P95_MS };
        var fullPathPass1 = fullPath1 is { P95Ms: <= TARGET_CACHED_FULL_PATH_P95_MS };
        var fullPathPass10 = fullPath10 is { P95Ms: <= TARGET_CACHED_FULL_PATH_P95_MS };
        var lexicalPass1 = lexical1 is { P95Ms: <= TARGET_LEXICAL_FALLBACK_P95_MS };
        var lexicalPass10 = lexical10 is { P95Ms: <= TARGET_LEXICAL_FALLBACK_P95_MS };

        targetDetails.Add($"DB Hybrid Retrieval c1: p95={hybrid1?.P95Ms:F1}ms (Target <= {TARGET_DB_HYBRID_P95_MS:F0}ms) [{(dbHybridPass1 ? "PASS" : "FAIL")}]");
        targetDetails.Add($"DB Hybrid Retrieval c10: p95={hybrid10?.P95Ms:F1}ms (Target <= {TARGET_DB_HYBRID_P95_MS:F0}ms) [{(dbHybridPass10 ? "PASS" : "FAIL")}]");
        targetDetails.Add($"Full Catalog (Cached Vector) c1: p95={fullPath1?.P95Ms:F1}ms (Target <= {TARGET_CACHED_FULL_PATH_P95_MS:F0}ms) [{(fullPathPass1 ? "PASS" : "FAIL")}]");
        targetDetails.Add($"Full Catalog (Cached Vector) c10: p95={fullPath10?.P95Ms:F1}ms (Target <= {TARGET_CACHED_FULL_PATH_P95_MS:F0}ms) [{(fullPathPass10 ? "PASS" : "FAIL")}]");
        targetDetails.Add($"Lexical Fallback c1: p95={lexical1?.P95Ms:F1}ms (Target <= {TARGET_LEXICAL_FALLBACK_P95_MS:F0}ms) [{(lexicalPass1 ? "PASS" : "FAIL")}]");
        targetDetails.Add($"Lexical Fallback c10: p95={lexical10?.P95Ms:F1}ms (Target <= {TARGET_LEXICAL_FALLBACK_P95_MS:F0}ms) [{(lexicalPass10 ? "PASS" : "FAIL")}]");

        var passed50KTargets = dbHybridPass1 && dbHybridPass10 && fullPathPass1 && fullPathPass10 && lexicalPass1 && lexicalPass10;

        if (!isPrescribed)
        {
            exactScanPassed = false;
            hnswRecommended = false;
            rolloutVerdict = "BLOCKED (Exploratory run with non-prescribed parameters; rollout verdict not permitted)";
            conclusion = $"Exploratory benchmark run completed with non-prescribed parameters (warmup={warmupCount}, samples={sampleCount}, sizes=[{string.Join(", ", corpusSizes)}], concurrency=[{string.Join(", ", concurrencyLevels)}]). Rollout / HNSW decision requires the exact prescribed protocol: warmup={PRESCRIBED_WARMUP}, samples={PRESCRIBED_SAMPLES}, sizes=[{string.Join(", ", _prescribedSizes)}], concurrency=[{string.Join(", ", _prescribedConcurrency)}]. No exact/HNSW rollout verdict permitted.";
        }
        else if (!isComplete)
        {
            exactScanPassed = false;
            hnswRecommended = false;
            rolloutVerdict = "BLOCKED (Evidence incomplete; rollout verdict not permitted)";
            conclusion = $"Cannot declare exact scan sufficiency: benchmark evidence is incomplete ({string.Join("; ", reasons)}). Complete prescribed measurements required.";
        }
        else if (passed50KTargets)
        {
            exactScanPassed = true;
            hnswRecommended = false;
            rolloutVerdict = "PERFORMANCE_TARGETS_MET (Exact scan meets retrieval latency targets at 50,000 documents; release rollout additionally requires release quality evaluation with human-adjudicated qrels)";
            conclusion = $"Exact scan PASSED target gates at 50,000 assets (DB Hybrid c1 p95: {hybrid1?.P95Ms:F1}ms, c10 p95: {hybrid10?.P95Ms:F1}ms <= {TARGET_DB_HYBRID_P95_MS:F0}ms; Full Catalog c1 p95: {fullPath1?.P95Ms:F1}ms, c10 p95: {fullPath10?.P95Ms:F1}ms <= {TARGET_CACHED_FULL_PATH_P95_MS:F0}ms; Lexical Fallback c1 p95: {lexical1?.P95Ms:F1}ms, c10 p95: {lexical10?.P95Ms:F1}ms <= {TARGET_LEXICAL_FALLBACK_P95_MS:F0}ms). Exact scan is sufficient; HNSW index is not currently required.";
        }
        else
        {
            exactScanPassed = false;
            hnswRecommended = false;
            var hybridFailed = !dbHybridPass1 || !dbHybridPass10;

            if (hybridFailed)
            {
                rolloutVerdict = "PERFORMANCE_TARGETS_FAILED (DB Hybrid retrieval exceeded 200 ms target at 50,000 documents; bottleneck attribution required via profile-sql. HNSW index cannot be recommended without isolated branch evidence. Release rollout additionally requires human-adjudicated qrels)";
                conclusion = $"Exact scan FAILED target gates at 50,000 assets due to DB Hybrid retrieval latency (c1 p95: {hybrid1?.P95Ms:F1}ms, c10 p95: {hybrid10?.P95Ms:F1}ms > {TARGET_DB_HYBRID_P95_MS:F0}ms). Bottleneck attribution via profile-sql is required before considering index changes; aggregate retrieval includes lexical search and entity hydration. HNSW index is not automatically recommended for aggregate failure. Do not implement HNSW, create an index, or generate a migration in this batch.";
            }
            else
            {
                rolloutVerdict = "PERFORMANCE_TARGETS_FAILED (Targets not met due to non-vector bottleneck; HNSW index is not indicated for lexical/handler failure. Release rollout additionally requires human-adjudicated qrels)";
                conclusion = $"Exact scan FAILED target gates at 50,000 assets due to non-vector retrieval bottleneck (Full Catalog c1 p95: {fullPath1?.P95Ms:F1}ms, c10 p95: {fullPath10?.P95Ms:F1}ms; Lexical Fallback c1 p95: {lexical1?.P95Ms:F1}ms, c10 p95: {lexical10?.P95Ms:F1}ms). DB Hybrid retrieval satisfied targets. Do not recommend HNSW for lexical/handler failure.";
            }
        }

        var decision = new BenchmarkPerformanceDecision(
            passed50KTargets,
            isPrescribed && isComplete,
            exactScanPassed,
            hnswRecommended,
            rolloutVerdict,
            qualityGateNote,
            targetDetails);

        return (protocol, completeness, decision, exactScanPassed, conclusion);
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

            result.Add((text, GenerateDeterministicVector(text, dimension)));
        }

        return result;
    }

    public static float[] GenerateDeterministicVector(string text, int dimension)
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

    public sealed class BenchmarkExecutionTracker
    {
        private int _hybridInvocations;
        private int _lexicalInvocations;
        private int _providerCalls;
        private int _resultCacheHits;
        private int _failedSamples;

        public int HybridInvocations => Volatile.Read(ref _hybridInvocations);
        public int LexicalInvocations => Volatile.Read(ref _lexicalInvocations);
        public int ProviderCalls => Volatile.Read(ref _providerCalls);
        public int ResultCacheHits => Volatile.Read(ref _resultCacheHits);
        public int FailedSamples => Volatile.Read(ref _failedSamples);

        public void RecordHybridInvocation() => Interlocked.Increment(ref _hybridInvocations);
        public void RecordLexicalInvocation() => Interlocked.Increment(ref _lexicalInvocations);
        public void RecordProviderCall() => Interlocked.Increment(ref _providerCalls);
        public void RecordResultCacheHit() => Interlocked.Increment(ref _resultCacheHits);
        public void RecordFailedSample() => Interlocked.Increment(ref _failedSamples);
    }

    public sealed class EvaluationObservingAssetStore : IAssetStore
    {
        private readonly IAssetStore _inner;
        private readonly BenchmarkExecutionTracker? _tracker;

        public EvaluationObservingAssetStore(IAssetStore inner, BenchmarkExecutionTracker? tracker)
        {
            _inner = inner;
            _tracker = tracker;
        }

        public async Task<CatalogPageResult<AssetListItem>> GetPaged(
            GetAssetsRequest request,
            float[]? queryEmbedding = null,
            string? modelKey = null,
            CancellationToken cancellationToken = default)
        {
            if (queryEmbedding is not null && !string.IsNullOrWhiteSpace(modelKey))
            {
                _tracker?.RecordHybridInvocation();
            }
            else
            {
                _tracker?.RecordLexicalInvocation();
            }

            return await _inner.GetPaged(request, queryEmbedding, modelKey, cancellationToken);
        }

        public Task<Asset> Add(Asset asset, CancellationToken cancellationToken = default) => _inner.Add(asset, cancellationToken);
        public Task<Asset> AddWithTags(Asset asset, List<Tag> tags, CancellationToken cancellationToken = default) => _inner.AddWithTags(asset, tags, cancellationToken);
        public Task<Asset> AddWithVersion(Asset asset, AssetVersion version, List<Tag>? tags, CancellationToken cancellationToken = default) => _inner.AddWithVersion(asset, version, tags, cancellationToken);
        public Task<Asset?> GetById(Guid id, CancellationToken cancellationToken = default) => _inner.GetById(id, cancellationToken);
        public Task<Asset?> GetById(Guid id, bool includeDeleted, CancellationToken cancellationToken = default) => _inner.GetById(id, includeDeleted, cancellationToken);
        public Task<Asset?> GetForUpdate(Guid id, CancellationToken cancellationToken = default) => _inner.GetForUpdate(id, cancellationToken);
        public Task<AssetCurrentVersionSnapshot?> GetCurrentVersionSnapshot(Guid assetId, CancellationToken cancellationToken = default) => _inner.GetCurrentVersionSnapshot(assetId, cancellationToken);
        public Task<AssetVersion?> GetVersion(Guid assetId, Guid versionId, CancellationToken cancellationToken = default) => _inner.GetVersion(assetId, versionId, cancellationToken);
        public Task<AssetOwnershipDto?> GetOwnership(Guid assetId, CancellationToken cancellationToken = default) => _inner.GetOwnership(assetId, cancellationToken);
        public Task<IReadOnlyList<AssetVersionSummaryDto>?> ListVersions(Guid assetId, Guid? requesterUserId, CancellationToken cancellationToken = default) => _inner.ListVersions(assetId, requesterUserId, cancellationToken);
        public Task<AssetVersion> CreateNextCandidateVersion(Guid assetId, Guid authorId, AssetVersion draft, CancellationToken cancellationToken = default) => _inner.CreateNextCandidateVersion(assetId, authorId, draft, cancellationToken);
        public Task<IReadOnlyList<string>> GetAllStorageKeys(Guid assetId, CancellationToken cancellationToken = default) => _inner.GetAllStorageKeys(assetId, cancellationToken);
        public Task<bool> ExistsByStorageKey(string storageKey, CancellationToken cancellationToken = default) => _inner.ExistsByStorageKey(storageKey, cancellationToken);
        public Task<AssetBlock.Domain.Core.Dto.Paging.PagedResult<SellerAssetListItem>> GetMyListings(Guid authorId, GetAssetsRequest request, CancellationToken cancellationToken = default) => _inner.GetMyListings(authorId, request, cancellationToken);
        public Task<SellerAssetDetailItem?> GetOwnedSellerDetail(Guid assetId, Guid ownerUserId, CancellationToken cancellationToken = default) => _inner.GetOwnedSellerDetail(assetId, ownerUserId, cancellationToken);
        public Task SoftDelete(Guid id, DateTimeOffset deletedAt, CancellationToken cancellationToken = default) => _inner.SoftDelete(id, deletedAt, cancellationToken);
        public Task Delete(Guid id, CancellationToken cancellationToken = default) => _inner.Delete(id, cancellationToken);
        public Task AddTag(Guid assetId, Guid tagId, CancellationToken cancellationToken = default) => _inner.AddTag(assetId, tagId, cancellationToken);
        public Task<bool> TryAddTag(Guid assetId, Guid tagId, CancellationToken cancellationToken = default) => _inner.TryAddTag(assetId, tagId, cancellationToken);
        public Task<bool> HasAssetTag(Guid assetId, Guid tagId, CancellationToken cancellationToken = default) => _inner.HasAssetTag(assetId, tagId, cancellationToken);
        public Task<bool> RemoveTag(Guid assetId, Guid tagId, CancellationToken cancellationToken = default) => _inner.RemoveTag(assetId, tagId, cancellationToken);
        public Task<bool> Update(Guid id, string? title, string? description, decimal? price, Guid? categoryId, CancellationToken cancellationToken = default) => _inner.Update(id, title, description, price, categoryId, cancellationToken);
        public Task<Guid?> GetPublicAnalyticsSellerId(Guid assetId, CancellationToken cancellationToken = default) => _inner.GetPublicAnalyticsSellerId(assetId, cancellationToken);
        public Task<Guid?> ResolveDownloadAnalyticsSellerId(Guid assetId, Guid assetVersionId, Guid actorUserId, CancellationToken cancellationToken = default) => _inner.ResolveDownloadAnalyticsSellerId(assetId, assetVersionId, actorUserId, cancellationToken);
    }

    public sealed class ObservableResultCache(BenchmarkExecutionTracker? tracker = null) : ITypedCache
    {
        private readonly BenchmarkExecutionTracker? _tracker = tracker;

        public Task<T?> Get<T>(string key, CancellationToken cancellationToken = default)
            where T : class
        {
            return Task.FromResult<T?>(null);
        }

        public Task Set<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
            where T : class => Task.CompletedTask;
    }

    public sealed class CachedVectorOnlyEmbeddingGenerator : ITextEmbeddingGenerator
    {
        private readonly BenchmarkExecutionTracker? _tracker;
        private int _invocationCount;

        public int InvocationCount => _invocationCount;

        public CachedVectorOnlyEmbeddingGenerator(BenchmarkExecutionTracker? tracker = null)
        {
            _tracker = tracker;
        }

        public Task<DomainModelVerificationResult> CheckModelAvailability(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            _tracker?.RecordProviderCall();
            return Task.FromResult(new DomainModelVerificationResult(true));
        }

        public Task<DomainGeneratedEmbedding> Generate(string text, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            _tracker?.RecordProviderCall();
            throw new InvalidOperationException("Cached-query-vector benchmark must not generate an embedding.");
        }
    }
}
