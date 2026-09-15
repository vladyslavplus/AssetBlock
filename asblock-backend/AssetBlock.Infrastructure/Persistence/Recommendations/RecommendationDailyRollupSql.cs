namespace AssetBlock.Infrastructure.Persistence.Recommendations;

internal static class RecommendationDailyRollupSql
{
    internal const string UPSERT_DAILY = """
        INSERT INTO recommendation_daily (
            "DayUtc", "SourceAssetId", "TargetAssetId", "RankingVersion",
            "ImpressionCount", "ClickCount", "UpdatedAt")
        SELECT
            {2}::date,
            "SourceAssetId",
            "TargetAssetId",
            "RankingVersion",
            COUNT(*) FILTER (WHERE "EventType" = 'IMPRESSION'),
            COUNT(*) FILTER (WHERE "EventType" = 'CLICK'),
            {3}
        FROM recommendation_events
        WHERE "OccurredAt" >= {0} AND "OccurredAt" < {1}
        GROUP BY "SourceAssetId", "TargetAssetId", "RankingVersion"
        ON CONFLICT ("DayUtc", "SourceAssetId", "TargetAssetId", "RankingVersion") DO UPDATE SET
            "ImpressionCount" = EXCLUDED."ImpressionCount",
            "ClickCount" = EXCLUDED."ClickCount",
            "UpdatedAt" = EXCLUDED."UpdatedAt"
        """;

    internal const string DELETE_STALE_DAILY = """
        DELETE FROM recommendation_daily rd
        WHERE rd."DayUtc" = {2}::date
          AND NOT EXISTS (
              SELECT 1
              FROM recommendation_events re
              WHERE re."SourceAssetId" = rd."SourceAssetId"
                AND re."TargetAssetId" = rd."TargetAssetId"
                AND re."RankingVersion" = rd."RankingVersion"
                AND re."OccurredAt" >= {0}
                AND re."OccurredAt" < {1}
          )
        """;

    internal const string DELETE_EXPIRED_EVENTS_BATCH = """
        DELETE FROM recommendation_events re
        WHERE re."Id" IN (
            SELECT e."Id"
            FROM recommendation_events e
            WHERE e."OccurredAt" < {0}
            ORDER BY e."OccurredAt", e."Id"
            LIMIT {1}
        )
        """;
}
