using System.Globalization;
using System.Text;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using AssetBlock.SearchEvaluation.Backfill;
using AssetBlock.SearchEvaluation.Benchmark;
using AssetBlock.SearchEvaluation.Evaluation;
using AssetBlock.SearchEvaluation.Metrics;
using AssetBlock.SearchEvaluation.Ollama;
using AssetBlock.SearchEvaluation.Profiling;
using AssetBlock.SearchEvaluation.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AssetBlock.SearchEvaluation;

public static class Program
{
    public const int EXIT_SUCCESS = 0;
    private const int EXIT_FAILURE = 1;
    public const int EXIT_MANUAL_EVALUATION_REQUIRED = 2;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("==========================================================");
        Console.WriteLine(" AssetBlock Search Evaluation Runner");
        Console.WriteLine("==========================================================");

        var mode = "deterministic";
        string? datasetPath = null;
        string? qrelsPath = null;
        string? configPath = null;

        var benchmarkSizes = new List<int> { 1000, 10000, 50000 };
        var warmupCount = 100;
        var sampleCount = 1000;
        var concurrencyLevels = new List<int> { 1, 10 };
        var skipOllama = false;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--mode" or "-m" && i + 1 < args.Length)
            {
                mode = args[++i].ToLowerInvariant();
            }
            else if (args[i] is "--dataset" or "-d" && i + 1 < args.Length)
            {
                datasetPath = args[++i];
            }
            else if (args[i] is "--qrels" or "-q" && i + 1 < args.Length)
            {
                qrelsPath = args[++i];
            }
            else if (args[i] is "--config" or "-c" && i + 1 < args.Length)
            {
                configPath = args[++i];
            }
            else if (args[i] is "--sizes" && i + 1 < args.Length)
            {
                benchmarkSizes = args[++i]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(int.Parse)
                    .ToList();
            }
            else if (args[i] is "--warmup" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], CultureInfo.InvariantCulture, out var w))
                {
                    warmupCount = w;
                }
            }
            else if (args[i] is "--samples" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], CultureInfo.InvariantCulture, out var s))
                {
                    sampleCount = s;
                }
            }
            else if (args[i] is "--concurrency" && i + 1 < args.Length)
            {
                concurrencyLevels = args[++i]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(int.Parse)
                    .ToList();
            }
            else if (args[i] is "--skip-ollama")
            {
                skipOllama = true;
            }
        }

        Console.WriteLine($"Mode: {mode}");
        Console.WriteLine();

        if (mode is "deterministic")
        {
            datasetPath ??= FindDatasetPath();
            if (datasetPath is null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("ERROR: Could not locate dataset.v1.json file.");
                Console.ResetColor();
                return EXIT_FAILURE;
            }

            Console.WriteLine($"Dataset path: {datasetPath}");
            Console.WriteLine("--> Validating dataset schema and integrity...");
            ValidationResult validation = DatasetValidator.ValidateFile(datasetPath);
            if (!validation.IsValid || validation.Dataset is null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Dataset validation failed with errors:");
                foreach (var err in validation.Errors)
                {
                    Console.WriteLine($"  - {err}");
                }
                Console.ResetColor();
                return EXIT_FAILURE;
            }

            DatasetV1Dto dataset = validation.Dataset;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] Dataset valid: {dataset.Documents.Count} documents, {dataset.Queries.Count} queries (Provenance: {dataset.Provenance}).");
            Console.ResetColor();
            Console.WriteLine();

            return RunDeterministicEvaluation(dataset);
        }
        else if (mode is "local-ollama")
        {
            datasetPath ??= FindDatasetPath();
            if (datasetPath is null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("ERROR: Could not locate dataset.v1.json file.");
                Console.ResetColor();
                return EXIT_FAILURE;
            }

            ValidationResult validation = DatasetValidator.ValidateFile(datasetPath);
            if (!validation.IsValid || validation.Dataset is null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Dataset validation failed with errors:");
                foreach (var err in validation.Errors)
                {
                    Console.WriteLine($"  - {err}");
                }
                Console.ResetColor();
                return EXIT_FAILURE;
            }

            DatasetV1Dto dataset = validation.Dataset;
            EmbeddingOptions options = ResolveEmbeddingOptions(configPath);

            var validator = new EmbeddingOptionsValidator();
            ValidateOptionsResult validationResult = validator.Validate(null, options);

            if (validationResult.Failed)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[BLOCKED / CONFIGURATION VALIDATION FAILED]");
                Console.WriteLine("Embedding configuration is incomplete, invalid, or missing provenance requirements:");
                if (validationResult.Failures != null)
                {
                    foreach (var failure in validationResult.Failures)
                    {
                        Console.WriteLine($"  - {failure}");
                    }
                }
                else
                {
                    Console.WriteLine($"  - {validationResult.FailureMessage}");
                }
                Console.WriteLine();
                Console.WriteLine("Prerequisites for local model evaluation:");
                Console.WriteLine("  1. Local Ollama daemon running on loopback (e.g. http://127.0.0.1:11434).");
                Console.WriteLine("  2. Installed candidate model with pinned non-floating tag (e.g. bge-m3:q8_0).");
                Console.WriteLine("  3. Exact model revision string and SHA-256 digest (sha256:<64 hex>).");
                Console.WriteLine("  4. Positive embedding dimension D.");
                Console.WriteLine("  5. Independent human-reviewed relevance judgments.");
                Console.WriteLine("No models are pulled or downloaded automatically.");
                Console.ResetColor();
                return EXIT_MANUAL_EVALUATION_REQUIRED;
            }

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
            var client = new LocalOllamaEmbeddingClient(httpClient, options);

            return await LocalOllamaEvaluator.RunEvaluationAsync(dataset, qrelsPath, options, client);
        }
        else if (mode is "benchmark")
        {
            EmbeddingOptions options = ResolveEmbeddingOptions(configPath);
            using HttpClient? httpClient = skipOllama ? null : new HttpClient();
            IOllamaEmbeddingClient? client = null;
            if (httpClient is not null)
            {
                httpClient.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
                client = new LocalOllamaEmbeddingClient(httpClient, options);
            }

            return await SearchBenchmarkRunner.RunBenchmarkAsync(
                benchmarkSizes,
                warmupCount,
                sampleCount,
                concurrencyLevels,
                options,
                client,
                skipOllama);
        }
        else if (mode is "profile-sql")
        {
            try
            {
                SearchSqlProfiler.ValidateProfilingArguments(
                    benchmarkSizes,
                    warmupCount,
                    sampleCount,
                    concurrencyLevels);
            }
            catch (ArgumentException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] Invalid profiling CLI arguments: {ex.Message}");
                Console.ResetColor();
                return EXIT_FAILURE;
            }

            EmbeddingOptions options = ResolveEmbeddingOptions(configPath);
            return await SearchSqlProfiler.RunProfilingAsync(
                benchmarkSizes,
                warmupCount,
                sampleCount,
                concurrencyLevels,
                skipOllama,
                options);
        }
        else if (mode is "backfill-evidence")
        {
            EmbeddingOptions options = ResolveEmbeddingOptions(configPath);
            return await BackfillEvidenceRunner.RunBackfillEvidenceAsync(options);
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Unknown mode: {mode}. Supported modes: deterministic, local-ollama, benchmark, backfill-evidence, profile-sql");
            Console.ResetColor();
            return EXIT_FAILURE;
        }
    }

    public static EmbeddingOptions ResolveEmbeddingOptions(string? configPath)
    {
        ConfigurationBuilder configBuilder = new();

        configPath ??= FindAppSettingsPath();
        if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
        {
            throw new FileNotFoundException($"AppSettings configuration file not found at: '{configPath}'");
        }

        configPath = Path.GetFullPath(configPath);
        Console.WriteLine($"Loading embedding configuration from: {configPath}");
        configBuilder.AddJsonFile(configPath, optional: false);

        IConfiguration configuration = configBuilder.Build();
        EmbeddingOptions options = new();
        configuration.GetSection(EmbeddingOptions.CONFIGURATION_PATH).Bind(options);

        // Explicitly enforce that provenance is defined in appsettings
        options.Provider = "Ollama";
        options.Enabled = true;

        Console.WriteLine("Pinned Model Provenance (Loaded from AppSettings):");
        Console.WriteLine($"  Model:     {options.Model}");
        Console.WriteLine($"  Revision:  {options.Revision}");
        Console.WriteLine($"  Digest:    {options.Digest}");
        Console.WriteLine($"  Dimension: {options.Dimension}");
        Console.WriteLine($"  BaseUrl:   {options.BaseUrl}");
        Console.WriteLine();

        return options;
    }

    private static string? FindAppSettingsPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "asblock-backend", "AssetBlock.WebApi", "appsettings.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "AssetBlock.WebApi", "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AssetBlock.WebApi", "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "asblock-backend", "AssetBlock.WebApi", "appsettings.json")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        return null;
    }

    private static int RunDeterministicEvaluation(DatasetV1Dto dataset)
    {
        return RunDeterministicEvaluation(dataset, out _);
    }

    public static int RunDeterministicEvaluation(DatasetV1Dto dataset, out MacroMetricsSummary overallSummary)
    {
        Console.WriteLine("--> Running deterministic metric validation & lexical baseline simulation...");

        // Precompute canonical metadata for all documents to ensure canonicalizer works on all dataset documents
        var canonicalDocs = new Dictionary<string, (string CanonicalText, string Hash)>();
        foreach (DatasetDocumentDto doc in dataset.Documents)
        {
            CanonicalPublicMetadataResult canon = AssetPublicMetadataCanonicalizer.Canonicalize(
                doc.Title,
                doc.Description,
                doc.Category,
                doc.Tags);

            canonicalDocs[doc.Key] = (canon.CanonicalText, canon.ContentHash);
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[PASS] Canonicalized {canonicalDocs.Count} documents into asset-public-metadata-v1 format.");
        Console.ResetColor();

        // Simulate deterministic rankings using deterministic lexical similarity heuristics
        var allQueryMetrics = new List<QueryEvaluationMetrics>();

        foreach (DatasetQueryDto query in dataset.Queries)
        {
            var queryTerms = query.Text
                .ToLowerInvariant()
                .Split([' ', '-', '_', ',', '.', '!', '?'], StringSplitOptions.RemoveEmptyEntries);

            var scoredDocs = new List<(string DocKey, double Score)>();

            foreach (DatasetDocumentDto doc in dataset.Documents)
            {
                var docText = $"{doc.Title} {doc.Description} {doc.Category} {string.Join(" ", doc.Tags)}".ToLowerInvariant();
                var matchCount = 0.0;

                foreach (var term in queryTerms)
                {
                    if (docText.Contains(term, StringComparison.Ordinal))
                    {
                        matchCount += 1.0;
                    }
                }

                scoredDocs.Add((doc.Key, matchCount));
            }

            // Top 20 retrieved
            var retrievedKeys = scoredDocs
                .OrderByDescending(d => d.Score)
                .ThenBy(d => d.DocKey, StringComparer.Ordinal)
                .Take(20)
                .Select(d => d.DocKey)
                .ToList();

            var groundTruth = query.Judgments.ToDictionary(j => j.DocumentKey, j => j.Relevance);

            var ndcgAt10 = SearchMetrics.CalculateNdcgAt10(retrievedKeys, groundTruth);
            var recallAt20 = SearchMetrics.CalculateRecallAt20(retrievedKeys, groundTruth);
            var mrr = SearchMetrics.CalculateMrr(retrievedKeys, groundTruth);

            allQueryMetrics.Add(new QueryEvaluationMetrics(
                query.Id,
                query.Language,
                query.Kind,
                ndcgAt10,
                recallAt20,
                mrr));
        }

        overallSummary = SearchMetrics.CalculateMacroAverage(allQueryMetrics);

        Console.WriteLine();
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine(" Deterministic Evaluation Results Summary");
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine($"Total Queries:     {overallSummary.QueryCount}");
        Console.WriteLine($"Macro nDCG@10:     {overallSummary.MeanNdcgAt10:F4}");
        Console.WriteLine($"Macro Recall@20:   {overallSummary.MeanRecallAt20:F4}");
        Console.WriteLine($"Macro MRR:         {overallSummary.MeanMrr:F4}");
        Console.WriteLine("----------------------------------------------------------");

        // Group by Language
        Console.WriteLine();
        Console.WriteLine("By Language Slice:");
        foreach (IGrouping<string, QueryEvaluationMetrics> group in allQueryMetrics.GroupBy(m => m.Language).OrderBy(g => g.Key))
        {
            MacroMetricsSummary summary = SearchMetrics.CalculateMacroAverage(group.ToList());
            Console.WriteLine($"  [{group.Key,-9}] Count: {summary.QueryCount,-3} | nDCG@10: {summary.MeanNdcgAt10:F4} | Recall@20: {summary.MeanRecallAt20:F4} | MRR: {summary.MeanMrr:F4}");
        }

        // Group by Kind
        Console.WriteLine();
        Console.WriteLine("By Query Kind:");
        foreach (IGrouping<string, QueryEvaluationMetrics> group in allQueryMetrics.GroupBy(m => m.Kind).OrderBy(g => g.Key))
        {
            MacroMetricsSummary summary = SearchMetrics.CalculateMacroAverage(group.ToList());
            Console.WriteLine($"  [{group.Key,-14}] Count: {summary.QueryCount,-3} | nDCG@10: {summary.MeanNdcgAt10:F4} | Recall@20: {summary.MeanRecallAt20:F4} | MRR: {summary.MeanMrr:F4}");
        }

        Console.WriteLine();
        Console.WriteLine("----------------------------------------------------------");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Automated deterministic simulation complete.");
        Console.ResetColor();
        Console.WriteLine("Pending Quality Gate Prerequisite:");
        Console.WriteLine("  1. Release-quality evaluation requires independent human-adjudicated qrels.");
        Console.WriteLine("----------------------------------------------------------");

        return EXIT_SUCCESS;
    }

    private static string? FindDatasetPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "dataset.v1.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "search-evaluation", "dataset.v1.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "search-evaluation", "dataset.v1.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "search-evaluation", "dataset.v1.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "asblock-backend", "search-evaluation", "dataset.v1.json")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }

        return null;
    }
}
