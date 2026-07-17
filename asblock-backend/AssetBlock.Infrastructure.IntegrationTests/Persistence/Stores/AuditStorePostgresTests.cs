using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class AuditStorePostgresTests(PostgresFixture fixture)
{
    private static AuditStore CreateStore(ApplicationDbContext db) => new(db);

    private static AuditWriter CreateWriter(ApplicationDbContext db) =>
        new(CreateStore(db), new NullAuditContextAccessor(), NullLogger<AuditWriter>.Instance);

    [Fact]
    public async Task Add_WhenSystemContext_ShouldPersistNullableActorAndRequestFields()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var writer = CreateWriter(db);

        await writer.Write(new AuditEvent(
            AuditActions.PAYMENT_PURCHASE_COMPLETED,
            AuditOutcome.SUCCESS,
            AuditResourceTypes.PURCHASE,
            Guid.NewGuid().ToString(),
            new Dictionary<string, object?> { ["assetId"] = Guid.NewGuid().ToString() },
            ActorTypeOverride: AuditActorType.SYSTEM), CancellationToken.None);

        var row = await db.AuditLogs.AsNoTracking().SingleAsync();
        row.ActorType.Should().Be(AuditActorType.SYSTEM);
        row.ActorUserId.Should().BeNull();
        row.TraceId.Should().BeNull();
        row.IpAddress.Should().BeNull();
        row.UserAgent.Should().BeNull();
        row.MetadataJson.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetPaged_WhenFiltersAndPaging_ShouldApplyInclusiveRangeAndStableOrder()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var store = CreateStore(db);
        var actor = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        var t1 = t0.AddMinutes(1);
        var t2 = t0.AddMinutes(2);

        db.AuditLogs.AddRange(
            new AuditLog
            {
                OccurredAt = t0,
                ActorType = AuditActorType.USER,
                ActorUserId = actor,
                Action = AuditActions.AUTH_LOGIN,
                Outcome = AuditOutcome.SUCCESS,
                ResourceType = AuditResourceTypes.USER,
                ResourceId = actor.ToString(),
                MetadataJson = """{"reasonCode":"ok"}"""
            },
            new AuditLog
            {
                OccurredAt = t1,
                ActorType = AuditActorType.USER,
                ActorUserId = actor,
                Action = AuditActions.ASSET_UPDATE,
                Outcome = AuditOutcome.DENIED,
                ResourceType = AuditResourceTypes.ASSET,
                ResourceId = Guid.NewGuid().ToString()
            },
            new AuditLog
            {
                OccurredAt = t2,
                ActorType = AuditActorType.ANONYMOUS,
                Action = AuditActions.AUTH_LOGIN,
                Outcome = AuditOutcome.FAILURE,
                ResourceType = AuditResourceTypes.USER,
                MetadataJson = """{"reasonCode":"ERR_AUTH_INVALID_CREDENTIALS"}"""
            });
        await db.SaveChangesAsync();

        var page = await store.GetPaged(new GetAuditLogsRequest
        {
            Page = 1,
            PageSize = 10,
            ActorUserId = actor,
            From = t0,
            To = t1
        });

        page.TotalCount.Should().Be(2);
        page.Items.Select(i => i.OccurredAt).Should().BeInDescendingOrder();
        page.Items[0].Action.Should().Be(AuditActions.ASSET_UPDATE);
        page.Items[0].Metadata.Should().BeNull();
        page.Items[1].Action.Should().Be(AuditActions.AUTH_LOGIN);
        page.Items[1].Metadata.Should().NotBeNull();
        page.Items[1].Metadata!.Value.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);
    }

    [Fact]
    public async Task Transaction_WhenRolledBack_ShouldRemoveBusinessAndAuditRows()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var unitOfWork = new EfUnitOfWork(db);
        var writer = CreateWriter(db);

        var act = async () => await unitOfWork.ExecuteInTransaction(async ct =>
        {
            db.Assets.Add(TestData.CreateAsset(author.Id, category.Id, title: "Audit Rollback Asset"));
            await db.SaveChangesAsync(ct);
            await writer.Write(new AuditEvent(
                AuditActions.ASSET_CREATE,
                AuditOutcome.SUCCESS,
                AuditResourceTypes.ASSET,
                Guid.NewGuid().ToString(),
                new Dictionary<string, object?> { ["categoryId"] = category.Id.ToString(), ["tagCount"] = 0 }), ct);
            throw new InvalidOperationException("force rollback");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await db.Assets.CountAsync()).Should().Be(0);
        (await db.AuditLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Transaction_WhenCommitted_ShouldKeepBusinessAndAuditRows()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var unitOfWork = new EfUnitOfWork(db);
        var writer = CreateWriter(db);
        var assetId = Guid.NewGuid();

        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            db.Assets.Add(TestData.CreateAsset(author.Id, category.Id, title: "Audit Commit Asset", id: assetId));
            await db.SaveChangesAsync(ct);
            await writer.Write(new AuditEvent(
                AuditActions.ASSET_CREATE,
                AuditOutcome.SUCCESS,
                AuditResourceTypes.ASSET,
                assetId.ToString(),
                new Dictionary<string, object?> { ["categoryId"] = category.Id.ToString(), ["tagCount"] = 0 }), ct);
        });

        (await db.Assets.CountAsync(a => a.Id == assetId)).Should().Be(1);
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync();
        audit.Action.Should().Be(AuditActions.ASSET_CREATE);
        audit.ResourceId.Should().Be(assetId.ToString());
    }

    [Fact]
    public async Task Add_WhenMetadataIsNotObject_ShouldFailDatabaseConstraint()
    {
        await using var db = await fixture.CreateCleanDbContext();
        db.AuditLogs.Add(new AuditLog
        {
            OccurredAt = DateTimeOffset.UtcNow,
            ActorType = AuditActorType.SYSTEM,
            Action = AuditActions.AUTH_LOGIN,
            Outcome = AuditOutcome.FAILURE,
            ResourceType = AuditResourceTypes.USER,
            MetadataJson = "[]"
        });

        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
