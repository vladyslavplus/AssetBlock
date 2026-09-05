using System.Text;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using AssetBlock.SearchEvaluation.Evaluation;
using AssetBlock.SearchEvaluation.Metrics;
using AssetBlock.SearchEvaluation.Ollama;
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
        string? configPath = null;
        string? modelOverride = null;
        string? revisionOverride = null;
        string? digestOverride = null;
        int? dimensionOverride = null;
        string? baseUrlOverride = null;
        int? timeoutOverride = null;

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
            else if (args[i] is "--config" or "-c" && i + 1 < args.Length)
            {
                configPath = args[++i];
            }
            else if (args[i] is "--model" && i + 1 < args.Length)
            {
                modelOverride = args[++i];
            }
            else if (args[i] is "--revision" && i + 1 < args.Length)
            {
                revisionOverride = args[++i];
            }
            else if (args[i] is "--digest" && i + 1 < args.Length)
            {
                digestOverride = args[++i];
            }
            else if (args[i] is "--dimension" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var dim))
                {
                    dimensionOverride = dim;
                }
            }
            else if (args[i] is "--base-url" && i + 1 < args.Length)
            {
                baseUrlOverride = args[++i];
            }
            else if (args[i] is "--timeout-seconds" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var timeoutSec))
                {
                    timeoutOverride = timeoutSec;
                }
            }
        }

        datasetPath ??= FindDatasetPath();
        if (datasetPath is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: Could not locate dataset.v1.json file.");
            Console.ResetColor();
            return EXIT_FAILURE;
        }

        Console.WriteLine($"Dataset path: {datasetPath}");
        Console.WriteLine($"Mode: {mode}");
        Console.WriteLine();

        // 1. Validate dataset schema and integrity
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
        Console.WriteLine($"[PASS] Dataset valid: {dataset.Documents.Count} documents, {dataset.Queries.Count} queries.");
        Console.ResetColor();
        Console.WriteLine();

        if (mode is "deterministic")
        {
            return RunDeterministicEvaluation(dataset);
        }
        else if (mode is "local-ollama")
        {
            EmbeddingOptions options = ResolveEmbeddingOptions(
                configPath,
                modelOverride,
                revisionOverride,
                digestOverride,
                dimensionOverride,
                baseUrlOverride,
                timeoutOverride);

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
                else if (!string.IsNullOrWhiteSpace(validationResult.FailureMessage))
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

            return await LocalOllamaEvaluator.RunEvaluationAsync(dataset, options, client);
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Unknown mode: {mode}. Supported modes: deterministic, local-ollama");
            Console.ResetColor();
            return EXIT_FAILURE;
        }
    }

    public static EmbeddingOptions ResolveEmbeddingOptions(
        string? configPath,
        string? modelOverride,
        string? revisionOverride,
        string? digestOverride,
        int? dimensionOverride,
        string? baseUrlOverride,
        int? timeoutOverride)
    {
        ConfigurationBuilder configBuilder = new();

        configPath ??= FindAppSettingsPath();
        if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
        {
            configBuilder.AddJsonFile(configPath, optional: true);
        }

        configBuilder.AddEnvironmentVariables();

        IConfiguration configuration = configBuilder.Build();
        EmbeddingOptions options = new();
        configuration.GetSection(EmbeddingOptions.CONFIGURATION_PATH).Bind(options);

        // Apply CLI overrides if specified
        if (!string.IsNullOrWhiteSpace(modelOverride))
        {
            options.Model = modelOverride.Trim();
        }
        if (!string.IsNullOrWhiteSpace(revisionOverride))
        {
            options.Revision = revisionOverride.Trim();
        }
        if (!string.IsNullOrWhiteSpace(digestOverride))
        {
            options.Digest = digestOverride.Trim();
        }
        if (dimensionOverride is > 0)
        {
            options.Dimension = dimensionOverride.Value;
        }
        if (!string.IsNullOrWhiteSpace(baseUrlOverride))
        {
            options.BaseUrl = baseUrlOverride.Trim();
        }
        if (timeoutOverride is > 0)
        {
            options.RequestTimeoutSeconds = timeoutOverride.Value;
        }

        // When evaluating local-ollama, provider is Ollama and mode is enabled
        options.Provider = "Ollama";
        options.Enabled = true;

        return options;
    }

    private static string? FindAppSettingsPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "asblock-backend", "AssetBlock.WebApi", "appsettings.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "AssetBlock.WebApi", "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AssetBlock.WebApi", "appsettings.json")
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
        Console.WriteLine("Automated validation complete.");
        Console.ResetColor();
        Console.WriteLine("Pending Manual Work:");
        Console.WriteLine("  1. Candidate multilingual model selection (installed locally via Ollama by operator).");
        Console.WriteLine("  2. Exact model tag, revision, digest, and dimension verification.");
        Console.WriteLine("  3. Independent human-reviewed relevance judgments.");
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
