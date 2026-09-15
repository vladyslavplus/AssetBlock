using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Recommendations;

public sealed record RecommendationDailyRecomputeResult(
    AnalyticsDailyRecomputeOutcome Outcome,
    int RowsUpserted);
