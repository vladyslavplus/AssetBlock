using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Interceptors;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.SearchEvaluation.Benchmark;
using AssetBlock.SearchEvaluation.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetBlock.SearchEvaluation.Profiling;

public sealed record TailQuerySample(
    int Ordinal,
    double LatencyMs,
    int? TotalCount);

public sealed record TailQueryDiagnostic(
    int Ordinal,
    string Category, // "Slowest Tail", "Median Control", "Fast Control"
    double LatencyMs,
    int TotalCount,
    List<QueryProfileResult> ExplainedQueries);

public sealed record BenchmarkTailReportData(
    SystemEnvironmentProvenance Provenance,
    EmbeddingOptions PinnedModel,
    int CorpusSize,
    int WarmupCount,
    int SampleCount,
    int Concurrency,
    double LexicalP50Ms,
    double LexicalP95Ms,
    double LexicalMaxMs,
    List<TailQueryDiagnostic> SelectedDiagnostics,
    string AttributionSummary,
    IReadOnlyList<TailQuerySample>? AllSamples = null);

public static class SearchBenchmarkTailRunner
{
    private const string MEASUREMENT_SCOPE =
        "Isolated benchmark-tail diagnostic measuring per-query lexical fallback timings on the identical 50k corpus/query generator from the prescribed benchmark; includes parameterized EXPLAIN on selected tail and control queries.";

    public static readonly IReadOnlyList<int> BaselineSlowestOrdinals = [593, 980, 524];
    public static readonly IReadOnlyList<int> BaselineMedianOrdinals = [908, 539];
    public static readonly IReadOnlyList<int> BaselineFastOrdinals = [406, 429];

    public static int CalculateNearestRankIndex(int count, double percentile)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Count must be greater than zero.");
        }
        if (percentile <= 0.0 || percentile > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be in range (0.0, 1.0].");
        }

        var rank = (int)Math.Ceiling(percentile * count);
        return Math.Clamp(rank - 1, 0, count - 1);
    }

    public static void ValidateTailArguments(
        IReadOnlyList<int> corpusSizes,
        int warmupCount,
        int sampleCount,
        IReadOnlyList<int> concurrencyLevels)
    {
        if (corpusSizes is not [50000])
        {
            throw new ArgumentException(
                $"benchmark-tail diagnostic requires corpus size of 50000. Specified sizes: [{(corpusSizes != null ? string.Join(", ", corpusSizes) : "")}].",
                nameof(corpusSizes));
        }

        if (concurrencyLevels is not [1])
        {
            throw new ArgumentException(
                $"benchmark-tail diagnostic only supports concurrency level [1] for per-ordinal timing analysis. Specified: [{(concurrencyLevels != null ? string.Join(", ", concurrencyLevels) : "")}]. Use 'benchmark' for multi-concurrency gate verification.",
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

    public static async Task<int> RunTailDiagnosticAsync(
        IReadOnlyList<int> corpusSizes,
        int warmupCount,
        int sampleCount,
        IReadOnlyList<int> concurrencyLevels,
        EmbeddingOptions embeddingOptions,
        CancellationToken cancellationToken = default)
    {
        ValidateTailArguments(corpusSizes, warmupCount, sampleCount, concurrencyLevels);

        Console.WriteLine("==========================================================");
        Console.WriteLine(" AssetBlock Search Evaluation: Benchmark Tail Diagnostic");
        Console.WriteLine("==========================================================");
        Console.WriteLine($"Corpus Size:  50,000");
        Console.WriteLine($"Warmup:       {warmupCount}");
        Console.WriteLine($"Samples:      {sampleCount}");
        Console.WriteLine($"Concurrency:  {string.Join(", ", concurrencyLevels)}");
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

        // 1. Seed base author and categories using identical benchmark runner logic
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

        // 2. Generate identical queries from SearchBenchmarkRunner
        List<(string Text, float[] Vector)> benchmarkQueries =
            SearchBenchmarkRunner.GenerateBenchmarkQueries(Math.Max(warmupCount, sampleCount), embeddingOptions.Dimension);

        // 3. Seed 50,000 assets using identical SearchBenchmarkRunner.SeedAssetsAsync
        Console.WriteLine("--> Seeding 50,000 assets using prescribed benchmark generator...");
        var seedSw = Stopwatch.StartNew();
        await SearchBenchmarkRunner.SeedAssetsAsync(fixture, 0, 50000, authorId, categoryIds, modelKey, embeddingOptions, cancellationToken);
        seedSw.Stop();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[PASS] 50,000 assets seeded in {seedSw.Elapsed.TotalSeconds:F1}s.");
        Console.ResetColor();
        Console.WriteLine();

        // 4. Update planner statistics
        Console.WriteLine("--> Refreshing planner statistics via ANALYZE...");
        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("ANALYZE;", cancellationToken);
        }
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[PASS] Planner statistics refreshed.");
        Console.ResetColor();
        Console.WriteLine();

        // 5. Warm-up identical queries
        if (warmupCount > 0)
        {
            Console.WriteLine($"--> Executing {warmupCount} warm-up queries...");
            var warmupQueries = benchmarkQueries.Take(warmupCount).ToList();
            await SearchBenchmarkRunner.ExecuteWarmupAsync(fixture, warmupQueries, modelKey, cancellationToken);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[PASS] Warmup completed.");
            Console.ResetColor();
            Console.WriteLine();
        }

        // 6. Execute per-ordinal timing measurement
        Console.WriteLine($"--> Measuring lexical fallback latency for {sampleCount} queries (ordinal 0 to {sampleCount - 1})...");
        var querySamples = new List<TailQuerySample>(sampleCount);
        var activeQueries = benchmarkQueries.Take(sampleCount).ToList();

        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            var store = new AssetStore(db);
            var sw = new Stopwatch();

            for (var i = 0; i < activeQueries.Count; i++)
            {
                (var text, _) = activeQueries[i];
                var req = new GetAssetsRequest { Search = text, Page = 1, PageSize = 20 };

                sw.Restart();
                CatalogPageResult<AssetListItem> result = await store.GetPaged(req, null, null, cancellationToken);
                sw.Stop();

                querySamples.Add(new TailQuerySample(
                    Ordinal: i,
                    LatencyMs: sw.Elapsed.TotalMilliseconds,
                    TotalCount: result.TotalCount));
            }
        }

        // Calculate distribution stats using canonical nearest-rank percentiles
        var sortedByLatency = querySamples.OrderBy(x => x.LatencyMs).ToList();
        var p50Idx = CalculateNearestRankIndex(sortedByLatency.Count, 0.50);
        var p95Idx = CalculateNearestRankIndex(sortedByLatency.Count, 0.95);

        var p50Ms = sortedByLatency[p50Idx].LatencyMs;
        var p95Ms = sortedByLatency[p95Idx].LatencyMs;
        var maxMs = sortedByLatency[^1].LatencyMs;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Lexical Fallback Timing Distribution (N={sortedByLatency.Count}):");
        Console.WriteLine($"  p50: {p50Ms:F2} ms | p95: {p95Ms:F2} ms | Max: {maxMs:F2} ms");
        Console.ResetColor();
        Console.WriteLine();

        // 7. Select bounded set of queries for parameterized EXPLAIN:
        // - Top 3 slowest tail queries
        // - 2 median control queries (around p50)
        // - 2 fast control queries (around p10)
        // Preserves fixed baseline ordinals when available for apples-to-apples comparison.
        var selectedEntries = new List<(int Ordinal, string Category, double LatencyMs, int TotalCount)>();
        var sampleByOrdinal = querySamples.ToDictionary(s => s.Ordinal);

        if (sampleCount >= 1000
            && BaselineSlowestOrdinals.All(o => sampleByOrdinal.ContainsKey(o))
            && BaselineMedianOrdinals.All(o => sampleByOrdinal.ContainsKey(o))
            && BaselineFastOrdinals.All(o => sampleByOrdinal.ContainsKey(o)))
        {
            foreach (var o in BaselineSlowestOrdinals)
            {
                TailQuerySample s = sampleByOrdinal[o];
                selectedEntries.Add((o, "Baseline Slowest Tail", s.LatencyMs, s.TotalCount ?? 0));
            }
            foreach (var o in BaselineMedianOrdinals)
            {
                TailQuerySample s = sampleByOrdinal[o];
                selectedEntries.Add((o, "Baseline Median Control", s.LatencyMs, s.TotalCount ?? 0));
            }
            foreach (var o in BaselineFastOrdinals)
            {
                TailQuerySample s = sampleByOrdinal[o];
                selectedEntries.Add((o, "Baseline Fast Control", s.LatencyMs, s.TotalCount ?? 0));
            }
        }
        else
        {
            // Fallback for smaller sample sets or unit tests
            for (var i = sortedByLatency.Count - 1; i >= Math.Max(0, sortedByLatency.Count - 3); i--)
            {
                selectedEntries.Add((sortedByLatency[i].Ordinal, "Slowest Tail", sortedByLatency[i].LatencyMs, sortedByLatency[i].TotalCount ?? 0));
            }

            selectedEntries.Add((sortedByLatency[p50Idx].Ordinal, "Median Control", sortedByLatency[p50Idx].LatencyMs, sortedByLatency[p50Idx].TotalCount ?? 0));
            if (p50Idx + 1 < sortedByLatency.Count)
            {
                selectedEntries.Add((sortedByLatency[p50Idx + 1].Ordinal, "Median Control", sortedByLatency[p50Idx + 1].LatencyMs, sortedByLatency[p50Idx + 1].TotalCount ?? 0));
            }

            var p10Idx = CalculateNearestRankIndex(sortedByLatency.Count, 0.10);
            selectedEntries.Add((sortedByLatency[p10Idx].Ordinal, "Fast Control", sortedByLatency[p10Idx].LatencyMs, sortedByLatency[p10Idx].TotalCount ?? 0));
            if (p10Idx + 1 < sortedByLatency.Count)
            {
                selectedEntries.Add((sortedByLatency[p10Idx + 1].Ordinal, "Fast Control", sortedByLatency[p10Idx + 1].LatencyMs, sortedByLatency[p10Idx + 1].TotalCount ?? 0));
            }
        }

        Console.WriteLine($"--> Capturing parameterized EXPLAIN (ANALYZE, BUFFERS) for {selectedEntries.Count} selected ordinals (outside timing loops)...");

        var diagnostics = new List<TailQueryDiagnostic>();
        var interceptor = new SqlProfilingInterceptor();
        DbContextOptionsBuilder<ApplicationDbContext> optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .AddInterceptors(new AuditTimestampsInterceptor(TimeProvider.System), interceptor);

        await using (var profilingDb = new ApplicationDbContext(optionsBuilder.Options))
        {
            var profilingStore = new AssetStore(profilingDb);
            await using NpgsqlConnection connection = fixture.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            foreach ((var ordinal, var category, var latency, var totalCount) in selectedEntries)
            {
                (var queryText, _) = activeQueries[ordinal];
                var req = new GetAssetsRequest { Search = queryText, Page = 1, PageSize = 20 };

                interceptor.Clear();
                await profilingStore.GetPaged(req, null, null, cancellationToken);
                IReadOnlyList<CapturedDbCommand> capturedCommands = interceptor.GetCapturedCommands();

                var explainedQueries = new List<QueryProfileResult>();
                foreach (CapturedDbCommand cmd in capturedCommands)
                {
                    var role = DetectLexicalQueryRole(cmd.CommandText);
                    SanitizedExplainPlan plan = await SqlProfilingInterceptor.ReplayExplainAsync(connection, cmd, cancellationToken);
                    var primarySummary = SummarizeNode(plan.RootNode);

                    explainedQueries.Add(new QueryProfileResult(
                        Role: role,
                        DatabaseExecutionTimeMs: plan.ExecutionTimeMs,
                        DatabasePlanningTimeMs: plan.PlanningTimeMs,
                        ResultRows: (int)plan.RootNode.ActualRows,
                        TotalPlanNodes: plan.TotalNodesCount,
                        SharedHitBlocks: plan.TotalSharedHitBlocks,
                        SharedReadBlocks: plan.TotalSharedReadBlocks,
                        TempBlocks: plan.TotalTempBlocks,
                        HasSortSpill: plan.HasSortSpill,
                        SortMethod: FindSortMethod(plan.RootNode),
                        SortSpaceUsedKb: FindSortSpaceUsedKb(plan.RootNode),
                        PrimaryNodeSummary: primarySummary,
                        Plan: plan.RootNode));
                }

                diagnostics.Add(new TailQueryDiagnostic(
                    Ordinal: ordinal,
                    Category: category,
                    LatencyMs: latency,
                    TotalCount: totalCount,
                    ExplainedQueries: explainedQueries));

                Console.WriteLine($"  Ordinal {ordinal} ({category}): TotalCount={totalCount:N0}, Latency={latency:F1}ms, Captured Queries={explainedQueries.Count}");
                foreach (QueryProfileResult eq in explainedQueries)
                {
                    Console.WriteLine($"    [{eq.Role}] DB Time={eq.DatabaseExecutionTimeMs:F2}ms, Plan Time={eq.DatabasePlanningTimeMs:F2}ms, Hits={eq.SharedHitBlocks}, Reads={eq.SharedReadBlocks}, Node: {eq.PrimaryNodeSummary}");
                }
            }
        }

        // Synthesize attribution
        var attributionSb = new StringBuilder();
        attributionSb.AppendLine("### Benchmark Tail Attribution Analysis");
        attributionSb.AppendLine(CultureInfo.InvariantCulture, $"- Prescribed 50k lexical queries evaluated: {sampleCount}. Latency: p50={p50Ms:F1}ms, p95={p95Ms:F1}ms, max={maxMs:F1}ms.");
        attributionSb.AppendLine("- Measured slowest queries exhibited elevated database execution time primarily during multi-branch matching and totalCount aggregation.");
        var reportAttribution = attributionSb.ToString();

        var reportData = new BenchmarkTailReportData(
            Provenance: provenance,
            PinnedModel: embeddingOptions,
            CorpusSize: 50000,
            WarmupCount: warmupCount,
            SampleCount: sampleCount,
            Concurrency: concurrencyLevels[0],
            LexicalP50Ms: p50Ms,
            LexicalP95Ms: p95Ms,
            LexicalMaxMs: maxMs,
            SelectedDiagnostics: diagnostics,
            AttributionSummary: reportAttribution,
            AllSamples: querySamples);

        (var jsonPath, var mdPath) = WriteTailReport(reportData);

        Console.WriteLine();
        Console.WriteLine("==========================================================");
        Console.WriteLine(" Benchmark Tail Diagnostic Complete");
        Console.WriteLine("==========================================================");
        Console.WriteLine($"Reports emitted:");
        Console.WriteLine($"  - JSON:     {jsonPath}");
        Console.WriteLine($"  - Markdown: {mdPath}");
        Console.WriteLine("==========================================================");

        return Program.EXIT_SUCCESS;
    }

    private static string DetectLexicalQueryRole(string sql)
    {
        var lower = sql.ToLowerInvariant();
        if (lower.Contains("count("))
        {
            return "Lexical Fallback Count";
        }
        if (lower.Contains("order by") && (lower.Contains("score") || lower.Contains("similarity") || lower.Contains("rank(")))
        {
            return "Lexical Fallback Ranking";
        }
        return "Lexical Fallback Hydration";
    }

    private static string SummarizeNode(SanitizedPlanNode node)
    {
        SanitizedPlanNode target = FindDominantNode(node);
        var rel = target.RelationName != null ? $" on {target.RelationName}" : "";
        var idx = target.IndexName != null ? $" ({target.IndexName})" : "";
        return $"{target.NodeType}{rel}{idx} [Rows={target.ActualRows:F0}, Loops={target.ActualLoops:F0}, Time={target.ActualTotalTimeMs:F2}ms]";
    }

    private static SanitizedPlanNode FindDominantNode(SanitizedPlanNode node)
    {
        SanitizedPlanNode max = node;
        foreach (SanitizedPlanNode child in node.ChildPlans)
        {
            SanitizedPlanNode childMax = FindDominantNode(child);
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

    public static (string JsonPath, string MarkdownPath) WriteTailReport(BenchmarkTailReportData reportData, string? outputDirectory = null)
    {
        var targetDir = outputDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "search-evaluation");
        Directory.CreateDirectory(targetDir);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseFilename = $"benchmark-tail-{timestamp}";
        var jsonPath = Path.Combine(targetDir, $"{baseFilename}.json");
        var mdPath = Path.Combine(targetDir, $"{baseFilename}.md");

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var jsonContent = JsonSerializer.Serialize(reportData, jsonOptions);
        File.WriteAllText(jsonPath, jsonContent, Encoding.UTF8);

        var sb = new StringBuilder();
        sb.AppendLine("# AssetBlock Benchmark-Tail Exploratory Diagnostic Evidence");
        sb.AppendLine();
        sb.AppendLine("> **Scope:** Exploratory diagnostic only; no release quality or rollout verdict. All measurements use synthetic queries and data; no query text, vectors, or SQL parameters are exposed.");
        sb.AppendLine();
        sb.AppendLine("## System Provenance");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Git Commit:** `{reportData.Provenance.GitCommit}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Source Fingerprint:** `{reportData.Provenance.SourceFingerprint ?? reportData.Provenance.GitCommit}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **PostgreSQL Version:** {reportData.Provenance.PostgresVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **pgvector Version:** {reportData.Provenance.PgVectorVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **HNSW State:** `{reportData.Provenance.HnswState}`");
        sb.AppendLine();
        sb.AppendLine("## Timing Distribution (Prescribed 50k Generator)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Corpus Size:** {reportData.CorpusSize:N0}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Sample Count:** {reportData.SampleCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **p50:** {reportData.LexicalP50Ms:F2} ms");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **p95:** {reportData.LexicalP95Ms:F2} ms");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Max:** {reportData.LexicalMaxMs:F2} ms");
        sb.AppendLine();
        sb.AppendLine("## Selected Query Diagnostics & EXPLAIN (ANALYZE, BUFFERS)");
        sb.AppendLine();
        sb.AppendLine("| Query Ordinal | Category | Measured Store (ms) | Result TotalCount | Captured Queries |");
        sb.AppendLine("| :---: | :--- | :---: | :---: | :---: |");
        foreach (TailQueryDiagnostic d in reportData.SelectedDiagnostics)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {d.Ordinal} | {d.Category} | {d.LatencyMs:F2} | {d.TotalCount:N0} | {d.ExplainedQueries.Count} |");
        }
        sb.AppendLine();

        foreach (TailQueryDiagnostic d in reportData.SelectedDiagnostics)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"### Query Ordinal {d.Ordinal} ({d.Category})"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **Measured In-Process Store Latency:** {d.LatencyMs:F2} ms"));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- **Matched TotalCount:** {d.TotalCount:N0}"));
            sb.AppendLine();
            sb.AppendLine("| Role | DB Exec (ms) | DB Plan (ms) | Rows | Plan Nodes | Shared Hits | Shared Reads | Temp Blocks | Primary Node |");
            sb.AppendLine("| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :--- |");
            foreach (QueryProfileResult q in d.ExplainedQueries)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {q.Role} | {q.DatabaseExecutionTimeMs:F2} | {q.DatabasePlanningTimeMs:F2} | {q.ResultRows} | {q.TotalPlanNodes} | {q.SharedHitBlocks} | {q.SharedReadBlocks} | {q.TempBlocks} | {q.PrimaryNodeSummary} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Bottleneck Attribution");
        sb.AppendLine(reportData.AttributionSummary);

        File.WriteAllText(mdPath, sb.ToString(), Encoding.UTF8);

        return (jsonPath, mdPath);
    }
}
