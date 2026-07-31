using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class AnalyticsEventPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TryInsert_WhenEventIsNew_ShouldWriteRowAndReturnTrue()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var store = new AnalyticsEventStore(db);
        var analyticsEvent = CreateEvent();

        var inserted = await store.TryInsert(analyticsEvent);

        inserted.Should().BeTrue();
        var stored = await db.AnalyticsEvents.AsNoTracking().SingleAsync(e => e.Id == analyticsEvent.Id);
        stored.SellerId.Should().Be(analyticsEvent.SellerId);
        stored.EventType.Should().Be(AnalyticsEventType.ASSET_VIEW);
        stored.Source.Should().Be(AnalyticsTrafficSource.CATALOG);
        stored.DeviceClass.Should().Be(AnalyticsDeviceClass.DESKTOP);
        stored.AssetId.Should().Be(analyticsEvent.AssetId);
    }

    [Fact]
    public async Task TryInsert_WhenEventIdIsReplayed_ShouldReturnFalseAndKeepOneRow()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var store = new AnalyticsEventStore(db);
        var analyticsEvent = CreateEvent();
        await store.TryInsert(analyticsEvent);

        var replayed = await store.TryInsert(analyticsEvent);

        replayed.Should().BeFalse();
        (await db.AnalyticsEvents.AsNoTracking().CountAsync(e => e.Id == analyticsEvent.Id)).Should().Be(1);
    }

    [Theory]
    [InlineData(nameof(AnalyticsEventType.ASSET_VIEW), true, true, false, false)]
    [InlineData(nameof(AnalyticsEventType.ASSET_VIEW), false, false, false, false)]
    [InlineData(nameof(AnalyticsEventType.BUNDLE_VIEW), true, false, true, false)]
    [InlineData(nameof(AnalyticsEventType.COLLECTION_VIEW), false, false, false, false)]
    [InlineData(nameof(AnalyticsEventType.COLLECTION_ITEM_CLICK), false, false, false, true)]
    [InlineData(nameof(AnalyticsEventType.DOWNLOAD_REQUESTED), true, false, false, false)]
    public async Task Insert_WhenTargetShapeDoesNotMatchEventType_ShouldViolateCheckConstraint(
        string eventType,
        bool hasAsset,
        bool hasVersion,
        bool hasBundle,
        bool hasCollection)
    {
        await using var db = await fixture.CreateCleanDbContext();

        var act = () => InsertRaw(
            db,
            eventType,
            assetId: hasAsset ? Guid.NewGuid() : null,
            assetVersionId: hasVersion ? Guid.NewGuid() : null,
            bundleId: hasBundle ? Guid.NewGuid() : null,
            collectionId: hasCollection ? Guid.NewGuid() : null);

        await ShouldViolateCheck(act, "CK_analytics_events_target_shape");
    }

    [Fact]
    public async Task Insert_WhenEventTypeIsUnknown_ShouldViolateCheckConstraint()
    {
        await using var db = await fixture.CreateCleanDbContext();

        var act = () => InsertRaw(db, "PAGE_VIEW", assetId: Guid.NewGuid());

        await ShouldViolateCheck(act, "CK_analytics_events_EventType");
    }

    [Fact]
    public async Task Insert_WhenSourceIsUnknown_ShouldViolateCheckConstraint()
    {
        await using var db = await fixture.CreateCleanDbContext();

        var act = () => InsertRaw(
            db,
            nameof(AnalyticsEventType.ASSET_VIEW),
            assetId: Guid.NewGuid(),
            source: "NEWSLETTER");

        await ShouldViolateCheck(act, "CK_analytics_events_Source");
    }

    [Fact]
    public async Task Insert_WhenDeviceClassIsUnknown_ShouldViolateCheckConstraint()
    {
        await using var db = await fixture.CreateCleanDbContext();

        var act = () => InsertRaw(
            db,
            nameof(AnalyticsEventType.ASSET_VIEW),
            assetId: Guid.NewGuid(),
            deviceClass: "WATCH");

        await ShouldViolateCheck(act, "CK_analytics_events_DeviceClass");
    }

    [Fact]
    public async Task Insert_WhenReferrerHostIsEmpty_ShouldViolateCheckConstraint()
    {
        await using var db = await fixture.CreateCleanDbContext();

        var act = () => InsertRaw(
            db,
            nameof(AnalyticsEventType.ASSET_VIEW),
            assetId: Guid.NewGuid(),
            source: nameof(AnalyticsTrafficSource.EXTERNAL),
            referrerHost: string.Empty);

        await ShouldViolateCheck(act, "CK_analytics_events_ReferrerHost_length");
    }

    [Fact]
    public async Task Insert_WhenReferrerHostPresentWithoutExternalSource_ShouldViolateCheckConstraint()
    {
        await using var db = await fixture.CreateCleanDbContext();

        var act = () => InsertRaw(
            db,
            nameof(AnalyticsEventType.ASSET_VIEW),
            assetId: Guid.NewGuid(),
            source: nameof(AnalyticsTrafficSource.CATALOG),
            referrerHost: "blog.example.com");

        await ShouldViolateCheck(act, "CK_analytics_events_ReferrerHost_source");
    }

    [Fact]
    public async Task Migrate_WhenFreshDatabase_ShouldHaveNoForeignKeysFromAnalyticsEventsToProducts()
    {
        await using var db = await fixture.CreateCleanDbContext();

        var foreignKeys = await db.Database.SqlQueryRaw<string>(
                """
                SELECT tc.constraint_name AS "Value"
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON tc.constraint_name = kcu.constraint_name
                 AND tc.table_schema = kcu.table_schema
                WHERE tc.table_name = 'analytics_events'
                  AND tc.constraint_type = 'FOREIGN KEY'
                  AND kcu.column_name IN ('AssetId', 'AssetVersionId', 'BundleId', 'CollectionId', 'SellerId')
                """)
            .ToListAsync();

        foreignKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task Migrate_WhenFreshDatabase_ShouldCreateAnalyticsEventChecksAndIndexes()
    {
        await using var db = await fixture.CreateCleanDbContext();

        var checks = await db.Database.SqlQueryRaw<string>(
                """
                SELECT conname AS "Value"
                FROM pg_constraint
                WHERE contype = 'c'
                  AND conname IN (
                    'CK_analytics_events_EventType',
                    'CK_analytics_events_Source',
                    'CK_analytics_events_DeviceClass',
                    'CK_analytics_events_target_shape',
                    'CK_analytics_events_ReferrerHost_length',
                    'CK_analytics_events_ReferrerHost_source'
                  )
                """)
            .ToListAsync();
        checks.Should().HaveCount(6);

        var indexes = await db.Database.SqlQueryRaw<string>(
                """
                SELECT indexname AS "Value"
                FROM pg_indexes
                WHERE tablename = 'analytics_events'
                """)
            .ToListAsync();
        indexes.Should().Contain(
        [
            "IX_analytics_events_SellerId_OccurredAt_Id",
            "IX_analytics_events_SellerId_EventType_OccurredAt_Id",
            "IX_analytics_events_SellerId_VisitorId_OccurredAt",
            "IX_analytics_events_SellerId_SessionId_OccurredAt",
            "IX_analytics_events_SellerId_AssetId_OccurredAt",
            "IX_analytics_events_SellerId_BundleId_OccurredAt",
            "IX_analytics_events_SellerId_CollectionId_OccurredAt",
            "IX_analytics_events_OccurredAt_brin"
        ]);
    }

    private static async Task ShouldViolateCheck(Func<Task> act, string constraintName)
    {
        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        ex.Which.ConstraintName.Should().Be(constraintName);
    }

    /// <summary>
    /// Writes straight to the table so the database constraints are exercised without the store's
    /// own shaping getting in the way.
    /// </summary>
    private static Task InsertRaw(
        ApplicationDbContext db,
        string eventType,
        Guid? assetId = null,
        Guid? assetVersionId = null,
        Guid? bundleId = null,
        Guid? collectionId = null,
        string source = nameof(AnalyticsTrafficSource.CATALOG),
        string deviceClass = nameof(AnalyticsDeviceClass.DESKTOP),
        string? referrerHost = null)
    {
        return db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO analytics_events (
                "Id", "EventType", "OccurredAt", "SellerId", "VisitorId", "SessionId", "ActorUserId",
                "AssetId", "AssetVersionId", "BundleId", "CollectionId", "Source", "ReferrerHost", "DeviceClass")
            VALUES (
                {Guid.NewGuid()}, {eventType}, {DateTimeOffset.UtcNow}, {Guid.NewGuid()},
                {Guid.NewGuid()}, {Guid.NewGuid()}, {(Guid?)null}::uuid,
                {assetId}::uuid, {assetVersionId}::uuid, {bundleId}::uuid, {collectionId}::uuid,
                {source}, {referrerHost}, {deviceClass})
            """);
    }

    private static AnalyticsEvent CreateEvent() =>
        new()
        {
            Id = Guid.NewGuid(),
            EventType = AnalyticsEventType.ASSET_VIEW,
            OccurredAt = DateTimeOffset.UtcNow,
            SellerId = Guid.NewGuid(),
            VisitorId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            Source = AnalyticsTrafficSource.CATALOG,
            DeviceClass = AnalyticsDeviceClass.DESKTOP
        };
}
