using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

/// <summary>
/// Seeds a large unrelated corpus, then records EXPLAIN ANALYZE output, table
/// cardinalities, existing indexes, and whole-request latency for metadata vs. popularity.
/// </summary>
[Collection(nameof(PostgresStoreCollection))]
public sealed class PopularityRankingQueryMeasurementPostgresTests(PostgresFixture fixture)
{
    private const int CANDIDATE_COUNT = 100;
    private const int UNRELATED_SOURCES = 60;
    private const int UNRELATED_TARGETS_PER_SOURCE = 25;
    private const int WINDOW_DAYS = 30;
    private const int BULK_PRODUCTS = 1500;
    private const int WARMUP = 3;
    private const int SAMPLES = 21;

    [Fact]
    public async Task Measure_PopularityRankingQueries_ShouldRecordPlansCardinalitiesAndLatency()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Category other = TestData.CreateCategory("Bulk", "bulk");
        db.Categories.Add(other);

        Asset source = AddReady(db, author, category, "measure-source");
        Guid sourceId = source.Id;
        var candidateIds = new List<Guid>(CANDIDATE_COUNT);
        for (var i = 1; i <= CANDIDATE_COUNT; i++)
        {
            Asset candidate = AddReady(db, author, category, $"measure-{i:D3}");
            candidateIds.Add(candidate.Id);
        }

        var sellers = new List<Guid> { author.Id };
        for (var i = 0; i < 24; i++)
        {
            User seller = TestData.CreateUser($"seller{i}", $"seller{i}@example.test");
            db.Users.Add(seller);
            sellers.Add(seller.Id);
        }

        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        db.ChangeTracker.Clear();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly windowStart = today.AddDays(1 - SimilarAssetsConstants.POPULARITY_WINDOW_DAYS);
        windowStart.Should().Be(today.AddDays(1 - WINDOW_DAYS));

        SeedRecommendationDaily(db, sourceId, candidateIds, today);
        SeedProductDaily(db, sellers, candidateIds, today);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await SeedPurchases(db, author.Id, candidateIds);
        db.ChangeTracker.Clear();

        // ANALYZE only (no VACUUM): fresh tables have no bloat, and VACUUM cannot run
        // in a multi-statement pipeline. One statement per call.
        await db.Database.ExecuteSqlRawAsync("""ANALYZE "recommendation_daily";""");
        await db.Database.ExecuteSqlRawAsync("""ANALYZE "product_analytics_daily";""");
        await db.Database.ExecuteSqlRawAsync("""ANALYZE "purchases";""");

        var cardinalities = new
        {
            recommendationDaily = await db.RecommendationDaily.LongCountAsync(),
            productAnalyticsDaily = await db.ProductAnalyticsDaily.LongCountAsync(),
            purchases = await db.Purchases.LongCountAsync()
        };

        List<IndexEvidence> existingIndexes = await ReadIndexDefinitions(db);

        Guid sourceAssetId = source.Id;
        List<Guid> ids = candidateIds;

        var engagementSql = db.RecommendationDaily.AsNoTracking()
            .Where(d => d.SourceAssetId == sourceAssetId
                && d.DayUtc >= windowStart
                && ids.Contains(d.TargetAssetId))
            .GroupBy(d => d.TargetAssetId)
            .Select(g => new
            {
                TargetAssetId = g.Key,
                Clicks = g.Sum(x => x.ClickCount),
                Impressions = g.Sum(x => x.ImpressionCount)
            })
            .ToQueryString();

        var viewsSql = db.ProductAnalyticsDaily.AsNoTracking()
            .Where(p => p.ProductType == AnalyticsProductKind.ASSET
                && p.DayUtc >= windowStart
                && ids.Contains(p.ProductId))
            .GroupBy(p => p.ProductId)
            .Select(g => new
            {
                ProductId = g.Key,
                ViewCount = g.Sum(x => x.Views)
            })
            .ToQueryString();

        var unitsSql = db.Purchases.AsNoTracking()
            .Where(p => ids.Contains(p.AssetId))
            .GroupBy(p => p.AssetId)
            .Select(g => new
            {
                AssetId = g.Key,
                GrossUnits = g.LongCount()
            })
            .ToQueryString();

        var parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["windowstart"] = windowStart,
            ["sourceassetid"] = sourceAssetId,
            ["ids"] = ids,
            ["producttype"] = nameof(AnalyticsProductKind.ASSET)
        };

        PlanEvidence engagementPlan = await Explain(db, engagementSql, parameters);
        PlanEvidence viewsPlan = await Explain(db, viewsSql, parameters);
        PlanEvidence unitsPlan = await Explain(db, unitsSql, parameters);

        var store = new AssetStore(db);
        var popularityOptions = new SimilarAssetsQueryOptions(false, null, 0, null, UsePopularityRanking: true);
        ModeMeasurement metadata = await MeasureMode(store, sourceId, SimilarAssetsQueryOptions.MetadataOnly, "metadata");
        ModeMeasurement popularity = await MeasureMode(store, sourceId, popularityOptions, "popularity");

        metadata.ItemCount.Should().Be(6);
        popularity.ItemCount.Should().Be(6);
        cardinalities.recommendationDaily.Should().BeGreaterThan(40000);
        cardinalities.productAnalyticsDaily.Should().BeGreaterThan(40000);
        existingIndexes.Should().Contain(i =>
            i.IndexName == "IX_recommendation_daily_source_target_day");
        existingIndexes.Should().Contain(i =>
            i.IndexName == "IX_product_analytics_daily_type_product_day");
        engagementPlan.NodeType.Should().NotBeNullOrWhiteSpace();
        viewsPlan.NodeType.Should().NotBeNullOrWhiteSpace();
        unitsPlan.NodeType.Should().NotBeNullOrWhiteSpace();
    }

    private static void SeedRecommendationDaily(
        ApplicationDbContext db,
        Guid sourceId,
        List<Guid> candidateIds,
        DateOnly today)
    {
        var rows = new List<RecommendationDaily>(UNRELATED_SOURCES * UNRELATED_TARGETS_PER_SOURCE * WINDOW_DAYS + 5000);
        for (var s = 0; s < UNRELATED_SOURCES; s++)
        {
            var unrelatedSource = Guid.NewGuid();
            for (var t = 0; t < UNRELATED_TARGETS_PER_SOURCE; t++)
            {
                var unrelatedTarget = Guid.NewGuid();
                for (var d = 0; d < WINDOW_DAYS; d++)
                {
                    rows.Add(new RecommendationDaily
                    {
                        DayUtc = today.AddDays(-d),
                        SourceAssetId = unrelatedSource,
                        TargetAssetId = unrelatedTarget,
                        RankingVersion = RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
                        ImpressionCount = (s + t + d) % 4,
                        ClickCount = (s + t + d) % 7 == 0 ? 1 : 0,
                        UpdatedAt = DateTimeOffset.UtcNow
                    });
                }
            }
        }

        // Measured source: spread engagement over the window plus stale rows outside it.
        for (var d = 0; d < WINDOW_DAYS; d++)
        {
            rows.Add(new RecommendationDaily
            {
                DayUtc = today.AddDays(-d),
                SourceAssetId = sourceId,
                TargetAssetId = candidateIds[0],
                RankingVersion = RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
                ImpressionCount = 2,
                ClickCount = 1,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        for (var i = 1; i <= 5; i++)
        {
            rows.Add(new RecommendationDaily
            {
                DayUtc = today,
                SourceAssetId = sourceId,
                TargetAssetId = candidateIds[i],
                RankingVersion = RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
                ImpressionCount = 0,
                ClickCount = 6 - i,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        for (var s = 0; s < 5; s++)
        {
            var staleSource = Guid.NewGuid();
            for (var t = 0; t < UNRELATED_TARGETS_PER_SOURCE; t++)
            {
                var staleTarget = Guid.NewGuid();
                for (var d = 0; d < 20; d++)
                {
                    rows.Add(new RecommendationDaily
                    {
                        DayUtc = today.AddDays(-WINDOW_DAYS - 1 - d),
                        SourceAssetId = staleSource,
                        TargetAssetId = staleTarget,
                        RankingVersion = RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
                        ImpressionCount = 3,
                        ClickCount = 0,
                        UpdatedAt = DateTimeOffset.UtcNow
                    });
                }
            }
        }

        db.RecommendationDaily.AddRange(rows);
    }

    private static void SeedProductDaily(
        ApplicationDbContext db,
        List<Guid> sellers,
        List<Guid> candidateIds,
        DateOnly today)
    {
        var rows = new List<ProductAnalyticsDaily>(BULK_PRODUCTS * WINDOW_DAYS + 5000);
        for (var p = 0; p < BULK_PRODUCTS; p++)
        {
            var productId = Guid.NewGuid();
            Guid sellerId = sellers[p % sellers.Count];
            AnalyticsProductKind kind = p % 10 == 0 ? AnalyticsProductKind.BUNDLE : AnalyticsProductKind.ASSET;
            for (var d = 0; d < WINDOW_DAYS; d++)
            {
                rows.Add(new ProductAnalyticsDaily
                {
                    SellerId = sellerId,
                    DayUtc = today.AddDays(-d),
                    ProductType = kind,
                    ProductId = productId,
                    Views = (p + d) % 9,
                    DownloadRequests = 0,
                    UniqueVisitors = (p + d) % 5,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        for (var i = 0; i < 10; i++)
        {
            rows.Add(new ProductAnalyticsDaily
            {
                SellerId = sellers[0],
                DayUtc = today,
                ProductType = AnalyticsProductKind.ASSET,
                ProductId = candidateIds[i],
                Views = 50 - i,
                DownloadRequests = 0,
                UniqueVisitors = 10,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        for (var p = 0; p < 200; p++)
        {
            var productId = Guid.NewGuid();
            for (var d = 0; d < 10; d++)
            {
                rows.Add(new ProductAnalyticsDaily
                {
                    SellerId = sellers[p % sellers.Count],
                    DayUtc = today.AddDays(-WINDOW_DAYS - 1 - d),
                    ProductType = AnalyticsProductKind.ASSET,
                    ProductId = productId,
                    Views = 4,
                    DownloadRequests = 0,
                    UniqueVisitors = 2,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        db.ProductAnalyticsDaily.AddRange(rows);
    }

    private static async Task SeedPurchases(ApplicationDbContext db, Guid sellerId, List<Guid> candidateIds)
    {
        // Bulk purchase graph on a small set of real assets (FK-safe); the measured
        // candidates receive a deterministic gross-units spread.
        var bulkAssets = new List<Asset>();
        User bulkAuthor = TestData.CreateUser("bulk-author", "bulk-author@example.test");
        db.Users.Add(bulkAuthor);
        Category bulkCategory = TestData.CreateCategory("Bulk purchase", "bulk-purchase");
        db.Categories.Add(bulkCategory);
        await db.SaveChangesAsync();

        for (var i = 0; i < 50; i++)
        {
            Asset asset = TestData.CreateAsset(bulkAuthor.Id, bulkCategory.Id, $"bulk-purchase-{i:D3}");
            db.Assets.Add(asset);
            db.AssetVersions.Add(TestData.CreateAssetVersion(asset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY));
            bulkAssets.Add(asset);
        }

        await db.SaveChangesAsync();

        Dictionary<Guid, Guid> versionIds = await db.AssetVersions
            .Where(v => bulkAssets.Select(a => a.Id).Contains(v.AssetId) && v.IsCurrent)
            .ToDictionaryAsync(v => v.AssetId, v => v.Id);

        var buyers = new List<User>();
        for (var i = 0; i < 200; i++)
        {
            User buyer = TestData.CreateUser($"measure-buyer{i}", $"measure-buyer{i}@example.test");
            db.Users.Add(buyer);
            buyers.Add(buyer);
        }

        await db.SaveChangesAsync();

        for (var i = 0; i < buyers.Count; i++)
        {
            Asset asset = bulkAssets[i % bulkAssets.Count];
            Purchase purchase = TestData.CreatePurchase(buyers[i].Id, asset.Id, versionIds[asset.Id]);
            TestData.AddCompletedPurchase(db, purchase, asset.Title, bulkAuthor.Id);
        }

        Dictionary<Guid, Guid> candidateVersionIds = await db.AssetVersions
            .Where(v => candidateIds.Contains(v.AssetId) && v.IsCurrent)
            .ToDictionaryAsync(v => v.AssetId, v => v.Id);

        for (var i = 0; i < 5; i++)
        {
            for (var u = 0; u < 6 - i; u++)
            {
                User buyer = TestData.CreateUser($"candidate-buyer{i}-{u}", $"candidate-buyer{i}-{u}@example.test");
                db.Users.Add(buyer);
                await db.SaveChangesAsync();
                Purchase purchase = TestData.CreatePurchase(
                    buyer.Id,
                    candidateIds[i],
                    candidateVersionIds[candidateIds[i]]);
                TestData.AddCompletedPurchase(db, purchase, $"measure-{i + 1:D3}", sellerId);
            }
        }

        await db.SaveChangesAsync();
    }

    private static async Task<List<IndexEvidence>> ReadIndexDefinitions(ApplicationDbContext db)
    {
        var rows = new List<IndexEvidence>();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = (NpgsqlCommand)db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText =
                """
                SELECT tablename, indexname, indexdef
                FROM pg_indexes
                WHERE schemaname = 'public'
                  AND tablename IN ('recommendation_daily', 'product_analytics_daily', 'purchases')
                ORDER BY tablename, indexname;
                """;
            await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(new IndexEvidence(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2)));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return rows;
    }

    private static async Task<PlanEvidence> Explain(
        ApplicationDbContext db,
        string sql,
        Dictionary<string, object> parameters)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = (NpgsqlCommand)db.Database.GetDbConnection().CreateCommand();
#pragma warning disable CA2100 // Measurement fixture only: EXPLAIN over EF-generated SQL text; no user input participates.
            cmd.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql;
#pragma warning restore CA2100
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches(sql, "@[A-Za-z]\\w*"))
            {
                if (!seen.Add(match.Value))
                {
                    continue;
                }

                var key = match.Value[1..].ToLowerInvariant();
                if (!parameters.TryGetValue(key, out var value))
                {
                    throw new InvalidOperationException($"No measurement value mapped for EF parameter '{match.Value}'.");
                }

                cmd.Parameters.AddWithValue(match.Value, value);
            }

            var json = (string?)await cmd.ExecuteScalarAsync();
            json.Should().NotBeNullOrWhiteSpace();
            using var doc = JsonDocument.Parse(json);
            JsonElement plan = doc.RootElement[0].GetProperty("Plan");
            return new PlanEvidence(
                NodeType: plan.GetProperty("Node Type").GetString() ?? string.Empty,
                IndexName: plan.TryGetProperty("Index Name", out JsonElement indexName)
                    ? indexName.GetString()
                    : FindIndexName(plan),
                PlanShape: DescribePlan(plan),
                ActualRows: plan.GetProperty("Actual Rows").GetInt64(),
                SharedHitBlocks: plan.TryGetProperty("Shared Hit Blocks", out JsonElement hit)
                    ? hit.GetInt64()
                    : 0,
                SharedReadBlocks: plan.TryGetProperty("Shared Read Blocks", out JsonElement read)
                    ? read.GetInt64()
                    : 0,
                ExecutionTimeMs: doc.RootElement[0].GetProperty("Execution Time").GetDouble(),
                PlanningTimeMs: doc.RootElement[0].GetProperty("Planning Time").GetDouble());
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static string DescribePlan(JsonElement plan)
    {
        var node = plan.GetProperty("Node Type").GetString() ?? "?";
        if (plan.TryGetProperty("Index Name", out JsonElement indexName))
        {
            node += $" using {indexName.GetString()}";
        }
        else if (plan.TryGetProperty("Relation Name", out JsonElement relation))
        {
            node += $" on {relation.GetString()}";
        }

        if (plan.TryGetProperty("Plans", out JsonElement children))
        {
            var parts = new List<string>();
            foreach (JsonElement child in children.EnumerateArray())
            {
                parts.Add(DescribePlan(child));
            }

            node += " -> (" + string.Join(" + ", parts) + ")";
        }

        return node;
    }

    private static string? FindIndexName(JsonElement plan)
    {
        if (plan.TryGetProperty("Index Name", out JsonElement indexName))
        {
            return indexName.GetString();
        }

        if (plan.TryGetProperty("Plans", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                var found = FindIndexName(child);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static async Task<ModeMeasurement> MeasureMode(
        AssetStore store,
        Guid sourceId,
        SimilarAssetsQueryOptions options,
        string mode)
    {
        for (var i = 0; i < WARMUP; i++)
        {
            _ = await store.GetSimilarPublic(sourceId, 6, options);
        }

        var samples = new double[SAMPLES];
        var itemCount = 0;
        var stopwatch = new Stopwatch();
        for (var i = 0; i < SAMPLES; i++)
        {
            stopwatch.Restart();
            SimilarPublicAssetsResult? result = await store.GetSimilarPublic(sourceId, 6, options);
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
            itemCount = result?.Items.Count ?? 0;
        }

        Array.Sort(samples);
        return new ModeMeasurement(
            mode,
            itemCount,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            samples[^1],
            samples);
    }

    private static double Percentile(double[] sortedAscending, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile * sortedAscending.Length);
        var index = Math.Clamp(rank - 1, 0, sortedAscending.Length - 1);
        return sortedAscending[index];
    }

    private static Asset AddReady(
        ApplicationDbContext db,
        User author,
        Category category,
        string title)
    {
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title);
        db.Assets.Add(asset);
        db.AssetVersions.Add(TestData.CreateAssetVersion(
            asset.Id,
            isCurrent: true,
            processingStatus: AssetVersionProcessingStatus.READY));
        return asset;
    }

    private sealed record IndexEvidence(string Table, string IndexName, string Definition);

    private sealed record PlanEvidence(
        string NodeType,
        string? IndexName,
        string PlanShape,
        long ActualRows,
        long SharedHitBlocks,
        long SharedReadBlocks,
        double ExecutionTimeMs,
        double PlanningTimeMs);

    private sealed record ModeMeasurement(
        string Mode,
        int ItemCount,
        double P50Ms,
        double P95Ms,
        double MaxMs,
        double[] SamplesMs);
}
