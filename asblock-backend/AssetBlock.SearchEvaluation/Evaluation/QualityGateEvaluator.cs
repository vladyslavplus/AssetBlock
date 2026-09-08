using AssetBlock.SearchEvaluation.Metrics;

namespace AssetBlock.SearchEvaluation.Evaluation;

public sealed record QualityGateResult(
    string Name,
    string Scope,
    double LexicalValue,
    double HybridValue,
    double Delta,
    string Requirement,
    bool Passed);

public sealed record QualityEvaluationGateSummary(
    bool AllGatesPassed,
    List<QualityGateResult> GateResults);

public static class QualityGateEvaluator
{
    private const double ABSOLUTE_OVERALL_NDCG_THRESHOLD = 0.75;
    private const double ABSOLUTE_OVERALL_RECALL_THRESHOLD = 0.85;
    private const double ABSOLUTE_OVERALL_MRR_THRESHOLD = 0.70;

    private const double ABSOLUTE_SLICE_NDCG_THRESHOLD = 0.65;
    private const double ABSOLUTE_SLICE_RECALL_THRESHOLD = 0.75;
    private const double ABSOLUTE_SLICE_MRR_THRESHOLD = 0.60;

    private const double MAX_ALLOWED_NDCG_REGRESSION = 0.02;
    private const double REQUIRED_IMPROVEMENT_DELTA = 0.05;
    private const double EPSILON = 1e-9;

    public static QualityEvaluationGateSummary Evaluate(
        List<QueryEvaluationMetrics> lexicalMetrics,
        List<QueryEvaluationMetrics> hybridMetrics)
    {
        var gates = new List<QualityGateResult>();

        MacroMetricsSummary overallLex = SearchMetrics.CalculateMacroAverage(lexicalMetrics);
        MacroMetricsSummary overallHyb = SearchMetrics.CalculateMacroAverage(hybridMetrics);

        // 1. Overall Absolute Release Gates
        gates.Add(new QualityGateResult(
            "Overall Macro nDCG@10 Absolute",
            "Overall",
            overallLex.MeanNdcgAt10,
            overallHyb.MeanNdcgAt10,
            overallHyb.MeanNdcgAt10,
            ">= 0.75",
            overallHyb.MeanNdcgAt10 >= ABSOLUTE_OVERALL_NDCG_THRESHOLD - EPSILON));

        gates.Add(new QualityGateResult(
            "Overall Macro Recall@20 Absolute",
            "Overall",
            overallLex.MeanRecallAt20,
            overallHyb.MeanRecallAt20,
            overallHyb.MeanRecallAt20,
            ">= 0.85",
            overallHyb.MeanRecallAt20 >= ABSOLUTE_OVERALL_RECALL_THRESHOLD - EPSILON));

        gates.Add(new QualityGateResult(
            "Overall Macro MRR Absolute",
            "Overall",
            overallLex.MeanMrr,
            overallHyb.MeanMrr,
            overallHyb.MeanMrr,
            ">= 0.70",
            overallHyb.MeanMrr >= ABSOLUTE_OVERALL_MRR_THRESHOLD - EPSILON));

        // 2. Overall Comparative Release Gates
        var ndcgDelta = overallHyb.MeanNdcgAt10 - overallLex.MeanNdcgAt10;
        gates.Add(new QualityGateResult(
            "Overall Macro nDCG@10 Regression",
            "Overall",
            overallLex.MeanNdcgAt10,
            overallHyb.MeanNdcgAt10,
            ndcgDelta,
            ">= Lexical - 0.02",
            ndcgDelta >= -MAX_ALLOWED_NDCG_REGRESSION - EPSILON));

        var recallDelta = overallHyb.MeanRecallAt20 - overallLex.MeanRecallAt20;
        gates.Add(new QualityGateResult(
            "Overall Macro Recall@20 Comparative",
            "Overall",
            overallLex.MeanRecallAt20,
            overallHyb.MeanRecallAt20,
            recallDelta,
            ">= Lexical",
            recallDelta >= -EPSILON));

        var mrrDelta = overallHyb.MeanMrr - overallLex.MeanMrr;
        gates.Add(new QualityGateResult(
            "Overall Macro MRR Comparative",
            "Overall",
            overallLex.MeanMrr,
            overallHyb.MeanMrr,
            mrrDelta,
            ">= Lexical",
            mrrDelta >= -EPSILON));

        // 3. Language Slices Absolute Gates (.65 / .75 / .60)
        var languageSlices = new (string key, string displayName)[]
        {
            ("en", "English"),
            ("uk", "Ukrainian"),
            ("technical", "Technical"),
            ("mixed", "Mixed")
        };

        foreach ((var langKey, var displayName) in languageSlices)
        {
            EvaluateLanguageSliceAbsolute(gates, langKey, displayName, hybridMetrics);
        }

        // 4. Per-Language Comparative Slices (Allowed regression <= 0.02)
        EvaluateLanguageSliceComparative(gates, "en", "English", lexicalMetrics, hybridMetrics);
        EvaluateLanguageSliceComparative(gates, "uk", "Ukrainian", lexicalMetrics, hybridMetrics);

        // 5. Slices Requiring Measurable Semantic Improvement (>= +0.05)
        // - Cross-Language is a query Kind slice
        // - Technical is a query Language slice
        EvaluateKindImprovementSlice(gates, "cross-language", "Cross-Language", lexicalMetrics, hybridMetrics);
        EvaluateLanguageImprovementSlice(gates, "technical", "Technical", lexicalMetrics, hybridMetrics);

        var allPassed = gates.All(g => g.Passed);
        return new QualityEvaluationGateSummary(allPassed, gates);
    }

    private static void EvaluateLanguageSliceAbsolute(
        List<QualityGateResult> gates,
        string langKey,
        string displayName,
        List<QueryEvaluationMetrics> hybrid)
    {
        var hybSlice = hybrid.Where(m => string.Equals(m.Language, langKey, StringComparison.OrdinalIgnoreCase)).ToList();

        if (hybSlice.Count == 0)
        {
            gates.Add(new QualityGateResult(
                $"{displayName} Slice Absolute nDCG@10",
                langKey, 0, 0, 0, ">= 0.65", false));
            gates.Add(new QualityGateResult(
                $"{displayName} Slice Absolute Recall@20",
                langKey, 0, 0, 0, ">= 0.75", false));
            gates.Add(new QualityGateResult(
                $"{displayName} Slice Absolute MRR",
                langKey, 0, 0, 0, ">= 0.60", false));
            return;
        }

        MacroMetricsSummary hybSummary = SearchMetrics.CalculateMacroAverage(hybSlice);

        gates.Add(new QualityGateResult(
            $"{displayName} Slice Absolute nDCG@10",
            langKey,
            0,
            hybSummary.MeanNdcgAt10,
            hybSummary.MeanNdcgAt10,
            ">= 0.65",
            hybSummary.MeanNdcgAt10 >= ABSOLUTE_SLICE_NDCG_THRESHOLD - EPSILON));

        gates.Add(new QualityGateResult(
            $"{displayName} Slice Absolute Recall@20",
            langKey,
            0,
            hybSummary.MeanRecallAt20,
            hybSummary.MeanRecallAt20,
            ">= 0.75",
            hybSummary.MeanRecallAt20 >= ABSOLUTE_SLICE_RECALL_THRESHOLD - EPSILON));

        gates.Add(new QualityGateResult(
            $"{displayName} Slice Absolute MRR",
            langKey,
            0,
            hybSummary.MeanMrr,
            hybSummary.MeanMrr,
            ">= 0.60",
            hybSummary.MeanMrr >= ABSOLUTE_SLICE_MRR_THRESHOLD - EPSILON));
    }

    private static void EvaluateLanguageSliceComparative(
        List<QualityGateResult> gates,
        string langKey,
        string displayName,
        List<QueryEvaluationMetrics> lexical,
        List<QueryEvaluationMetrics> hybrid)
    {
        var lexSlice = lexical.Where(m => string.Equals(m.Language, langKey, StringComparison.OrdinalIgnoreCase)).ToList();
        var hybSlice = hybrid.Where(m => string.Equals(m.Language, langKey, StringComparison.OrdinalIgnoreCase)).ToList();

        if (lexSlice.Count == 0 || hybSlice.Count == 0)
        {
            gates.Add(new QualityGateResult(
                $"{displayName} Slice nDCG@10 Regression",
                langKey,
                0, 0, 0,
                ">= Lexical - 0.02",
                false));
            return;
        }

        MacroMetricsSummary lexSummary = SearchMetrics.CalculateMacroAverage(lexSlice);
        MacroMetricsSummary hybSummary = SearchMetrics.CalculateMacroAverage(hybSlice);
        var delta = hybSummary.MeanNdcgAt10 - lexSummary.MeanNdcgAt10;

        gates.Add(new QualityGateResult(
            $"{displayName} Slice nDCG@10 Regression",
            langKey,
            lexSummary.MeanNdcgAt10,
            hybSummary.MeanNdcgAt10,
            delta,
            ">= Lexical - 0.02",
            delta >= -MAX_ALLOWED_NDCG_REGRESSION - EPSILON));
    }

    private static void EvaluateKindImprovementSlice(
        List<QualityGateResult> gates,
        string kindKey,
        string displayName,
        List<QueryEvaluationMetrics> lexical,
        List<QueryEvaluationMetrics> hybrid)
    {
        var lexSlice = lexical.Where(m => string.Equals(m.Kind, kindKey, StringComparison.OrdinalIgnoreCase)).ToList();
        var hybSlice = hybrid.Where(m => string.Equals(m.Kind, kindKey, StringComparison.OrdinalIgnoreCase)).ToList();

        if (lexSlice.Count == 0 || hybSlice.Count == 0)
        {
            gates.Add(new QualityGateResult(
                $"{displayName} Kind Slice Improvement",
                kindKey,
                0, 0, 0,
                ">= Lexical + 0.05",
                false));
            return;
        }

        MacroMetricsSummary lexSummary = SearchMetrics.CalculateMacroAverage(lexSlice);
        MacroMetricsSummary hybSummary = SearchMetrics.CalculateMacroAverage(hybSlice);
        var delta = hybSummary.MeanNdcgAt10 - lexSummary.MeanNdcgAt10;

        gates.Add(new QualityGateResult(
            $"{displayName} Kind Slice Improvement",
            kindKey,
            lexSummary.MeanNdcgAt10,
            hybSummary.MeanNdcgAt10,
            delta,
            ">= Lexical + 0.05",
            delta >= REQUIRED_IMPROVEMENT_DELTA - EPSILON));
    }

    private static void EvaluateLanguageImprovementSlice(
        List<QualityGateResult> gates,
        string langKey,
        string displayName,
        List<QueryEvaluationMetrics> lexical,
        List<QueryEvaluationMetrics> hybrid)
    {
        var lexSlice = lexical.Where(m => string.Equals(m.Language, langKey, StringComparison.OrdinalIgnoreCase)).ToList();
        var hybSlice = hybrid.Where(m => string.Equals(m.Language, langKey, StringComparison.OrdinalIgnoreCase)).ToList();

        if (lexSlice.Count == 0 || hybSlice.Count == 0)
        {
            gates.Add(new QualityGateResult(
                $"{displayName} Language Slice Improvement",
                langKey,
                0, 0, 0,
                ">= Lexical + 0.05",
                false));
            return;
        }

        MacroMetricsSummary lexSummary = SearchMetrics.CalculateMacroAverage(lexSlice);
        MacroMetricsSummary hybSummary = SearchMetrics.CalculateMacroAverage(hybSlice);
        var delta = hybSummary.MeanNdcgAt10 - lexSummary.MeanNdcgAt10;

        gates.Add(new QualityGateResult(
            $"{displayName} Language Slice Improvement",
            langKey,
            lexSummary.MeanNdcgAt10,
            hybSummary.MeanNdcgAt10,
            delta,
            ">= Lexical + 0.05",
            delta >= REQUIRED_IMPROVEMENT_DELTA - EPSILON));
    }
}
