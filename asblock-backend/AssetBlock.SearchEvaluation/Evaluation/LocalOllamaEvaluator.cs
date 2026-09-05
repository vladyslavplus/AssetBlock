using System.Diagnostics;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.SearchEvaluation.Metrics;
using AssetBlock.SearchEvaluation.Ollama;
using AssetBlock.SearchEvaluation.Validation;
using AssetBlock.SearchEvaluation.VectorOperations;

namespace AssetBlock.SearchEvaluation.Evaluation;

public static class LocalOllamaEvaluator
{
    public static async Task<int> RunEvaluationAsync(
        DatasetV1Dto dataset,
        EmbeddingOptions options,
        IOllamaEmbeddingClient client,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine(" Candidate Model Provenance");
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine($"Provider:          {options.Provider}");
        Console.WriteLine($"Model:             {options.Model}");
        Console.WriteLine($"Revision:          {options.Revision}");
        Console.WriteLine($"Digest:            {options.Digest}");
        Console.WriteLine($"Dimension:         {options.Dimension}");
        Console.WriteLine($"Base URL:          {options.BaseUrl}");
        Console.WriteLine($"Timeout:           {options.RequestTimeoutSeconds}s");
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine();

        // 1. Verify model availability in local Ollama daemon
        Console.WriteLine($"--> Verifying local availability of candidate model '{options.Model}'...");
        ModelVerificationResult verification = await client.CheckModelAvailability(cancellationToken);
        if (!verification.IsAvailable)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[BLOCKED / MANUAL EVALUATION REQUIRED]");
            Console.WriteLine(verification.ErrorMessage);
            Console.WriteLine();
            Console.WriteLine("Prerequisites for local model evaluation:");
            Console.WriteLine($"  1. Local Ollama daemon running on loopback ({options.BaseUrl}).");
            Console.WriteLine($"  2. Installed candidate model with pinned non-floating tag ({options.Model}).");
            Console.WriteLine($"  3. Exact model revision string ({options.Revision}) and SHA-256 digest ({options.Digest}).");
            Console.WriteLine($"  4. Positive embedding dimension ({options.Dimension}).");
            Console.WriteLine("  5. Independent human-reviewed relevance judgments.");
            Console.WriteLine("No models are pulled or downloaded automatically.");
            Console.ResetColor();
            return Program.EXIT_MANUAL_EVALUATION_REQUIRED;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[PASS] Candidate model verified in local Ollama daemon.");
        if (!string.IsNullOrWhiteSpace(verification.ActualDigest))
        {
            Console.WriteLine($"  Verified digest: {verification.ActualDigest}");
        }
        Console.ResetColor();
        Console.WriteLine();

        // 2. Canonicalize documents and generate document embeddings
        Console.WriteLine($"--> Canonicalizing and sequentially embedding {dataset.Documents.Count} documents (1-by-1)...");
        var docVectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var docLatencies = new List<double>();
        var docStopwatch = new Stopwatch();

        foreach (DatasetDocumentDto doc in dataset.Documents)
        {
            CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize(
                doc.Title,
                doc.Description,
                doc.Category,
                doc.Tags);

            docStopwatch.Restart();
            var vector = await client.GenerateEmbedding(canonical.CanonicalText, cancellationToken);
            docStopwatch.Stop();

            docLatencies.Add(docStopwatch.Elapsed.TotalMilliseconds);
            docVectors[doc.Key] = vector;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[PASS] Generated sequential embeddings for {docVectors.Count} documents.");
        Console.ResetColor();
        Console.WriteLine();

        // 3. Normalize and generate query embeddings
        Console.WriteLine($"--> Normalizing and sequentially embedding {dataset.Queries.Count} queries (1-by-1)...");
        var queryVectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var queryLatencies = new List<double>();
        var queryStopwatch = new Stopwatch();

        foreach (DatasetQueryDto query in dataset.Queries)
        {
            var normalizedQuery = CatalogSearchNormalization.NormalizeSearchQuery(query.Text) ?? query.Text;

            queryStopwatch.Restart();
            var vector = await client.GenerateEmbedding(normalizedQuery, cancellationToken);
            queryStopwatch.Stop();

            queryLatencies.Add(queryStopwatch.Elapsed.TotalMilliseconds);
            queryVectors[query.Id] = vector;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[PASS] Generated sequential embeddings for {queryVectors.Count} queries.");
        Console.ResetColor();
        Console.WriteLine();

        // 4. Compute cosine similarity ranking and IR metrics
        Console.WriteLine("--> Computing semantic cosine similarity ranking and evaluation metrics...");
        var allQueryMetrics = new List<QueryEvaluationMetrics>();

        foreach (DatasetQueryDto query in dataset.Queries)
        {
            var qVector = queryVectors[query.Id];

            var rankedDocs = new List<(string DocKey, double Similarity)>(docVectors.Count);
            foreach (KeyValuePair<string, float[]> entry in docVectors)
            {
                var sim = VectorMath.CosineSimilarity(qVector, entry.Value);
                rankedDocs.Add((entry.Key, sim));
            }

            // Order by descending similarity, tie-break by document key ordinal
            var orderedKeys = rankedDocs
                .OrderByDescending(d => d.Similarity)
                .ThenBy(d => d.DocKey, StringComparer.Ordinal)
                .Take(20)
                .Select(d => d.DocKey)
                .ToList();

            var groundTruth = query.Judgments.ToDictionary(j => j.DocumentKey, j => j.Relevance);
            var ndcgAt10 = SearchMetrics.CalculateNdcgAt10(orderedKeys, groundTruth);
            var recallAt20 = SearchMetrics.CalculateRecallAt20(orderedKeys, groundTruth);
            var mrr = SearchMetrics.CalculateMrr(orderedKeys, groundTruth);

            allQueryMetrics.Add(new QueryEvaluationMetrics(
                query.Id,
                query.Language,
                query.Kind,
                ndcgAt10,
                recallAt20,
                mrr));
        }

        MacroMetricsSummary overallSummary = SearchMetrics.CalculateMacroAverage(allQueryMetrics);

        // 5. Output Summary Report
        Console.WriteLine();
        Console.WriteLine("==========================================================");
        Console.WriteLine(" Candidate Model Evaluation Results Summary");
        Console.WriteLine("==========================================================");
        Console.WriteLine($"Model:             {options.Model}");
        Console.WriteLine($"Revision:          {options.Revision}");
        Console.WriteLine($"Digest:            {options.Digest}");
        Console.WriteLine($"Dimension:         {options.Dimension}");
        Console.WriteLine($"Total Queries:     {overallSummary.QueryCount}");
        Console.WriteLine($"Macro nDCG@10:     {overallSummary.MeanNdcgAt10:F4}");
        Console.WriteLine($"Macro Recall@20:   {overallSummary.MeanRecallAt20:F4}");
        Console.WriteLine($"Macro MRR:         {overallSummary.MeanMrr:F4}");
        Console.WriteLine("----------------------------------------------------------");

        // Group by Language Slice
        Console.WriteLine();
        Console.WriteLine("By Language Slice:");
        foreach (IGrouping<string, QueryEvaluationMetrics> group in allQueryMetrics.GroupBy(m => m.Language).OrderBy(g => g.Key))
        {
            MacroMetricsSummary summary = SearchMetrics.CalculateMacroAverage(group.ToList());
            Console.WriteLine($"  [{group.Key,-9}] Count: {summary.QueryCount,-3} | nDCG@10: {summary.MeanNdcgAt10:F4} | Recall@20: {summary.MeanRecallAt20:F4} | MRR: {summary.MeanMrr:F4}");
        }

        // Group by Query Kind
        Console.WriteLine();
        Console.WriteLine("By Query Kind:");
        foreach (IGrouping<string, QueryEvaluationMetrics> group in allQueryMetrics.GroupBy(m => m.Kind).OrderBy(g => g.Key))
        {
            MacroMetricsSummary summary = SearchMetrics.CalculateMacroAverage(group.ToList());
            Console.WriteLine($"  [{group.Key,-14}] Count: {summary.QueryCount,-3} | nDCG@10: {summary.MeanNdcgAt10:F4} | Recall@20: {summary.MeanRecallAt20:F4} | MRR: {summary.MeanMrr:F4}");
        }

        // Latency Percentiles
        Console.WriteLine();
        Console.WriteLine("Latency Profile (Sequential 1-by-1 Evaluation):");
        (double Mean, double P50, double P95, double Min, double Max) docStats = CalculateLatencyStats(docLatencies);
        (double Mean, double P50, double P95, double Min, double Max) queryStats = CalculateLatencyStats(queryLatencies);

        Console.WriteLine($"  Doc Embedding   (Sequential, N={docLatencies.Count}): Mean: {docStats.Mean:F1} ms | p50: {docStats.P50:F1} ms | p95: {docStats.P95:F1} ms | Min: {docStats.Min:F1} ms | Max: {docStats.Max:F1} ms");
        Console.WriteLine($"  Query Embedding (Sequential, N={queryLatencies.Count}): Mean: {queryStats.Mean:F1} ms | p50: {queryStats.P50:F1} ms | p95: {queryStats.P95:F1} ms | Min: {queryStats.Min:F1} ms | Max: {queryStats.Max:F1} ms");

        Console.WriteLine();
        Console.WriteLine("----------------------------------------------------------");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("NOTE: Candidate model cannot be declared accepted without independent human-reviewed relevance judgments.");
        Console.ResetColor();
        Console.WriteLine("Automated validation complete.");
        Console.WriteLine("----------------------------------------------------------");

        return Program.EXIT_SUCCESS;
    }

    public static (double Mean, double P50, double P95, double Min, double Max) CalculateLatencyStats(List<double> latencies)
    {
        if (latencies.Count == 0)
        {
            return (0, 0, 0, 0, 0);
        }

        var sorted = latencies.OrderBy(x => x).ToList();
        var count = sorted.Count;
        var mean = sorted.Average();
        var min = sorted[0];
        var max = sorted[^1];

        // Nearest-rank method for p50 and p95
        var p50Index = (int)Math.Ceiling(0.50 * count) - 1;
        var p95Index = (int)Math.Ceiling(0.95 * count) - 1;

        p50Index = Math.Clamp(p50Index, 0, count - 1);
        p95Index = Math.Clamp(p95Index, 0, count - 1);

        return (mean, sorted[p50Index], sorted[p95Index], min, max);
    }
}
