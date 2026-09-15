using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Recommendations;

public sealed record PersonalRecomputeBatchResult(
    AnalyticsDailyRecomputeOutcome Outcome,
    int UsersRecomputed);
