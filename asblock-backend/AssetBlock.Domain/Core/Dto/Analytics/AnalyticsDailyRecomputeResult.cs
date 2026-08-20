using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Analytics;

public sealed record AnalyticsDailyRecomputeResult(
    AnalyticsDailyRecomputeOutcome Outcome,
    int SellerRowsUpserted,
    int ProductRowsUpserted,
    int CollectionRowsUpserted,
    int TrafficRowsUpserted);
