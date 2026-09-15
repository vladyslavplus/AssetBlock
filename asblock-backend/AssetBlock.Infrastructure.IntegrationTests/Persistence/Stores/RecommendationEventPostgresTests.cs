using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class RecommendationEventPostgresTests(PostgresFixture fixture)
{
    private static readonly DateOnly _dayUtc = new(2026, 9, 12);

    [Fact]
    public async Task TryInsert_WhenEventIdIsReplayed_ShouldReturnFalseAndKeepOneRow()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var store = new RecommendationEventStore(db);
        RecommendationEvent row = CreateEvent();

        (await store.TryInsert(row)).Should().BeTrue();
        (await store.TryInsert(row)).Should().BeFalse();
        (await db.RecommendationEvents.CountAsync(e => e.Id == row.Id)).Should().Be(1);
    }

    [Fact]
    public async Task TryInsert_WhenSameExposureTargetAndType_ShouldReturnFalse()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var store = new RecommendationEventStore(db);
        RecommendationEvent first = CreateEvent();
        RecommendationEvent duplicate = CreateEvent();
        duplicate.ExposureId = first.ExposureId;
        duplicate.TargetAssetId = first.TargetAssetId;
        duplicate.EventType = first.EventType;

        (await store.TryInsert(first)).Should().BeTrue();
        (await store.TryInsert(duplicate)).Should().BeFalse();
        (await db.RecommendationEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TryAcquireAndRecomputeDaily_ShouldCountImpressionsAndClicksWithoutVisitors()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var store = new RecommendationEventStore(db);
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await store.TryInsert(CreateEvent(source, target, RecommendationEventType.IMPRESSION, _dayUtc));
        await store.TryInsert(CreateEvent(source, target, RecommendationEventType.CLICK, _dayUtc));
        await store.TryInsert(CreateEvent(source, target, RecommendationEventType.IMPRESSION, _dayUtc.AddDays(1)));

        RecommendationDailyRecomputeResult result = await store.TryAcquireAndRecomputeDaily(
            _dayUtc.AddDays(1),
            _dayUtc,
            DateTimeOffset.UtcNow,
            120);

        result.Outcome.Should().Be(AnalyticsDailyRecomputeOutcome.COMPLETED);
        RecommendationDaily dayOne = await db.RecommendationDaily.AsNoTracking()
            .SingleAsync(r => r.DayUtc == _dayUtc && r.SourceAssetId == source);
        dayOne.ImpressionCount.Should().Be(1);
        dayOne.ClickCount.Should().Be(1);
        RecommendationDaily dayTwo = await db.RecommendationDaily.AsNoTracking()
            .SingleAsync(r => r.DayUtc == _dayUtc.AddDays(1) && r.SourceAssetId == source);
        dayTwo.ImpressionCount.Should().Be(1);
        dayTwo.ClickCount.Should().Be(0);
    }

    [Fact]
    public async Task TryAcquireAndDeleteExpiredEvents_ShouldDeleteOnlyBeforeCutoff()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var store = new RecommendationEventStore(db);
        RecommendationEvent oldEvent = CreateEvent(occurredDay: _dayUtc);
        RecommendationEvent fresh = CreateEvent(occurredDay: _dayUtc.AddDays(RecommendationTelemetryConstants.RAW_EVENT_RETENTION_DAYS));
        await store.TryInsert(oldEvent);
        await store.TryInsert(fresh);

        DateTimeOffset cutoff = DayStart(_dayUtc.AddDays(1));
        AnalyticsEventRetentionResult deleted = await store.TryAcquireAndDeleteExpiredEvents(cutoff, 100, 5, 120);

        deleted.LockAcquired.Should().BeTrue();
        deleted.DeletedCount.Should().Be(1);
        (await db.RecommendationEvents.AsNoTracking().Select(e => e.Id).SingleAsync()).Should().Be(fresh.Id);
    }

    private static RecommendationEvent CreateEvent(
        Guid? sourceId = null,
        Guid? targetId = null,
        RecommendationEventType eventType = RecommendationEventType.IMPRESSION,
        DateOnly? occurredDay = null)
    {
        DateTimeOffset occurredAt = DayStart(occurredDay ?? _dayUtc).AddHours(3);
        return new RecommendationEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            OccurredAt = occurredAt,
            ExposureId = Guid.NewGuid(),
            SourceAssetId = sourceId ?? Guid.NewGuid(),
            TargetAssetId = targetId ?? Guid.NewGuid(),
            SlotPosition = 0,
            RankingVersion = RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
            VisitorId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            DeviceClass = AnalyticsDeviceClass.DESKTOP
        };
    }

    private static DateTimeOffset DayStart(DateOnly day) =>
        new(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
}
