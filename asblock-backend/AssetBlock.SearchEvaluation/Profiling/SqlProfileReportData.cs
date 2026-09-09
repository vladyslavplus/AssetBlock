using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.SearchEvaluation.Infrastructure;

namespace AssetBlock.SearchEvaluation.Profiling;

public sealed record SanitizedPlanNode(
    string NodeType,
    string? RelationName,
    string? IndexName,
    string? ParentRelationship,
    string? JoinType,
    string? SortMethod,
    string? SortSpaceType,
    long? SortSpaceUsedKb,
    long? PeakMemoryUsageKb,
    bool? ParallelAware,
    bool? AsyncCapable,
    double StartupCost,
    double TotalCost,
    double PlanRows,
    int PlanWidth,
    double ActualStartupTimeMs,
    double ActualTotalTimeMs,
    double ActualRows,
    double ActualLoops,
    long SharedHitBlocks,
    long SharedReadBlocks,
    long SharedDirtiedBlocks,
    long SharedWrittenBlocks,
    long TempReadBlocks,
    long TempWrittenBlocks,
    List<SanitizedPlanNode> ChildPlans);

public sealed record SanitizedExplainPlan(
    double PlanningTimeMs,
    double ExecutionTimeMs,
    SanitizedPlanNode RootNode)
{
    public int TotalNodesCount => 1 + CountChildNodes(RootNode);

    // PostgreSQL EXPLAIN (ANALYZE, BUFFERS) root node includes statement-level inclusive buffer totals.
    // Reading directly from RootNode avoids double-counting parent and child plans.
    public long TotalSharedHitBlocks => RootNode.SharedHitBlocks;
    public long TotalSharedReadBlocks => RootNode.SharedReadBlocks;
    public long TotalTempBlocks => RootNode.TempReadBlocks + RootNode.TempWrittenBlocks;
    public bool HasSortSpill => AnyNode(RootNode, n => (n.TempReadBlocks + n.TempWrittenBlocks > 0) || string.Equals(n.SortSpaceType, "Disk", StringComparison.OrdinalIgnoreCase));

    private static int CountChildNodes(SanitizedPlanNode node)
    {
        var count = 0;
        foreach (SanitizedPlanNode child in node.ChildPlans)
        {
            count += 1 + CountChildNodes(child);
        }
        return count;
    }

    private static bool AnyNode(SanitizedPlanNode node, Func<SanitizedPlanNode, bool> predicate)
    {
        if (predicate(node))
        {
            return true;
        }
        foreach (SanitizedPlanNode child in node.ChildPlans)
        {
            if (AnyNode(child, predicate))
            {
                return true;
            }
        }
        return false;
    }
}

public sealed record QueryProfileResult(
    string Role,
    double DatabaseExecutionTimeMs,
    double DatabasePlanningTimeMs,
    int ResultRows,
    int TotalPlanNodes,
    long SharedHitBlocks,
    long SharedReadBlocks,
    long TempBlocks,
    bool HasSortSpill,
    string? SortMethod,
    long? SortSpaceUsedKb,
    string PrimaryNodeSummary,
    SanitizedPlanNode Plan);

public sealed record ScenarioLatencyMetrics(
    int SampleCount,
    double MeanMs,
    double P50Ms,
    double P95Ms,
    double MinMs,
    double MaxMs);

public sealed record ScenarioProfileResult(
    string ScenarioId,
    string Description,
    int FilterEligibleCount,
    int SemanticEligibleCount,
    int FusedResultCount,
    ScenarioLatencyMetrics HybridHandlerLatency,
    ScenarioLatencyMetrics HybridStoreLatency,
    ScenarioLatencyMetrics LexicalFallbackHandlerLatency,
    ScenarioLatencyMetrics LexicalFallbackStoreLatency,
    List<QueryProfileResult> HybridQueries,
    List<QueryProfileResult> LexicalFallbackQueries,
    string DominantBottleneckSummary);

public sealed record SqlProfileReportData(
    SystemEnvironmentProvenance Provenance,
    EmbeddingOptions ModelOptions,
    int DiagnosticWarmupCount,
    int DiagnosticSampleCount,
    int DiagnosticConcurrency,
    List<ScenarioProfileResult> Scenarios,
    Dictionary<string, int> NegativeFixtureCounts,
    string ActualIndexState,
    string MeasurementScope,
    string BottleneckAttributionSummary);
