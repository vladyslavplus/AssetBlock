namespace AssetBlock.SearchEvaluation.Metrics;

/// <summary>
/// Metric results for an individual search query.
/// </summary>
public sealed record QueryEvaluationMetrics(
    string QueryId,
    string Language,
    string Kind,
    double NdcgAt10,
    double RecallAt20,
    double Mrr);

/// <summary>
/// Macro-averaged metric summary.
/// </summary>
public sealed record MacroMetricsSummary(
    int QueryCount,
    double MeanNdcgAt10,
    double MeanRecallAt20,
    double MeanMrr);

/// <summary>
/// Deterministic search evaluation metrics: nDCG@10, Recall@20, MRR, and macro averages.
/// </summary>
public static class SearchMetrics
{
    private const int RELEVANCE_THRESHOLD = 2; // Grade >= 2 is relevant (0: irrelevant, 1: marginal, 2: relevant, 3: exact)

    /// <summary>
    /// Calculates Discounted Cumulative Gain at rank K.
    /// DCG@K = sum_{i=1}^K (2^rel_i - 1) / log2(i + 1)
    /// </summary>
    public static double CalculateDcgAtK(IReadOnlyList<string> retrievedDocKeys, IReadOnlyDictionary<string, int> groundTruth, int k = 10)
    {
        var dcg = 0.0;
        var limit = Math.Min(k, retrievedDocKeys.Count);

        for (var rank = 1; rank <= limit; rank++)
        {
            var docKey = retrievedDocKeys[rank - 1];
            if (groundTruth.TryGetValue(docKey, out var grade) && grade > 0)
            {
                var gain = Math.Pow(2, grade) - 1.0;
                var discount = Math.Log2(rank + 1);
                dcg += gain / discount;
            }
        }

        return dcg;
    }

    /// <summary>
    /// Calculates Ideal DCG at rank K by sorting all judged relevance scores descending.
    /// </summary>
    private static double CalculateIdcgAtK(IReadOnlyDictionary<string, int> groundTruth, int k = 10)
    {
        var sortedGrades = groundTruth.Values
            .Where(g => g > 0)
            .OrderByDescending(g => g)
            .Take(k)
            .ToList();

        var idcg = 0.0;
        for (var rank = 1; rank <= sortedGrades.Count; rank++)
        {
            var grade = sortedGrades[rank - 1];
            var gain = Math.Pow(2, grade) - 1.0;
            var discount = Math.Log2(rank + 1);
            idcg += gain / discount;
        }

        return idcg;
    }

    /// <summary>
    /// Calculates Normalized Discounted Cumulative Gain at rank 10 (nDCG@10).
    /// </summary>
    public static double CalculateNdcgAt10(IReadOnlyList<string> retrievedDocKeys, IReadOnlyDictionary<string, int> groundTruth)
    {
        var idcg = CalculateIdcgAtK(groundTruth, 10);
        if (idcg <= 0.0)
        {
            return 0.0;
        }

        var dcg = CalculateDcgAtK(retrievedDocKeys, groundTruth, 10);
        var ndcg = dcg / idcg;
        return Math.Clamp(ndcg, 0.0, 1.0);
    }

    /// <summary>
    /// Calculates Recall at rank 20 (Recall@20).
    /// Recall@20 = (relevant retrieved in top 20) / (all judged relevant with grade >= 2).
    /// </summary>
    public static double CalculateRecallAt20(IReadOnlyList<string> retrievedDocKeys, IReadOnlyDictionary<string, int> groundTruth)
    {
        var totalRelevant = groundTruth.Values.Count(g => g >= RELEVANCE_THRESHOLD);
        if (totalRelevant == 0)
        {
            return 0.0;
        }

        var retrievedTop20 = retrievedDocKeys.Take(20).ToHashSet();
        var relevantRetrieved = groundTruth
            .Count(kvp => kvp.Value >= RELEVANCE_THRESHOLD && retrievedTop20.Contains(kvp.Key));

        var recall = (double)relevantRetrieved / totalRelevant;
        return Math.Clamp(recall, 0.0, 1.0);
    }

    /// <summary>
    /// Calculates Mean Reciprocal Rank (MRR).
    /// MRR = 1 / rank of first retrieved result with grade >= 2, else 0.
    /// </summary>
    public static double CalculateMrr(IReadOnlyList<string> retrievedDocKeys, IReadOnlyDictionary<string, int> groundTruth)
    {
        for (var rank = 1; rank <= retrievedDocKeys.Count; rank++)
        {
            var docKey = retrievedDocKeys[rank - 1];
            if (groundTruth.TryGetValue(docKey, out var grade) && grade >= RELEVANCE_THRESHOLD)
            {
                return 1.0 / rank;
            }
        }

        return 0.0;
    }

    /// <summary>
    /// Computes macro average over a collection of query metric results.
    /// Macro average = sum(metric) / count(queries), never document-weighted.
    /// </summary>
    public static MacroMetricsSummary CalculateMacroAverage(IReadOnlyCollection<QueryEvaluationMetrics> queryMetrics)
    {
        if (queryMetrics.Count == 0)
        {
            return new MacroMetricsSummary(0, 0.0, 0.0, 0.0);
        }

        var totalNdcg = queryMetrics.Sum(m => m.NdcgAt10);
        var totalRecall = queryMetrics.Sum(m => m.RecallAt20);
        var totalMrr = queryMetrics.Sum(m => m.Mrr);
        var count = queryMetrics.Count;

        return new MacroMetricsSummary(
            count,
            totalNdcg / count,
            totalRecall / count,
            totalMrr / count);
    }
}
