using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class EmailDeliveryStorePostgresTests(PostgresFixture fixture)
{
    private static EmailDeliveryStore CreateStore(ApplicationDbContext db) =>
        new(db, NullLogger<EmailDeliveryStore>.Instance);

    [Fact]
    public async Task TryClaimDelivery_WhenConcurrentWorkersAttemptClaim_ExactlyOneSucceeds()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var outboxId = Guid.NewGuid();
        var messageId = $"<{outboxId:N}@mail.localhost>";
        var recipientUserId = Guid.NewGuid();
        const string recipientAddress = "concurrent@example.com";

        await using var dbA = fixture.CreateDbContext();
        await using var dbB = fixture.CreateDbContext();
        var storeA = CreateStore(dbA);
        var storeB = CreateStore(dbB);

        var taskA = storeA.TryClaimDelivery(outboxId, messageId, recipientAddress, recipientUserId, EmailTemplateKind.PURCHASE_RECEIPT, TimeSpan.FromMinutes(2));
        var taskB = storeB.TryClaimDelivery(outboxId, messageId, recipientAddress, recipientUserId, EmailTemplateKind.PURCHASE_RECEIPT, TimeSpan.FromMinutes(2));

        var results = await Task.WhenAll(taskA, taskB);

        var claimedCount = results.Count(r => r.Status == DeliveryClaimStatus.CLAIMED);
        var conflictCount = results.Count(r => r.Status == DeliveryClaimStatus.CONCURRENT_CONFLICT);

        claimedCount.Should().Be(1);
        conflictCount.Should().Be(1);
    }

    [Fact]
    public async Task ConfirmDelivery_WhenClaimTokenMatches_ShouldMarkDeliveredAndMakeSubsequentClaimsReturnAlreadyDelivered()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var store = CreateStore(db);

        var outboxId = Guid.NewGuid();
        var messageId = $"<{outboxId:N}@mail.localhost>";
        var recipientUserId = Guid.NewGuid();
        const string recipientAddress = "confirm@example.com";

        (DeliveryClaimStatus claimStatus, Guid? claimToken) = await store.TryClaimDelivery(
            outboxId,
            messageId,
            recipientAddress,
            recipientUserId,
            EmailTemplateKind.PURCHASE_RECEIPT,
            TimeSpan.FromMinutes(2));

        claimStatus.Should().Be(DeliveryClaimStatus.CLAIMED);
        claimToken.Should().NotBeNull();

        var deliveredAt = DateTimeOffset.UtcNow;
        var confirmed = await store.ConfirmDelivery(outboxId, claimToken.Value, deliveredAt);
        confirmed.Should().BeTrue();

        await using var verifyDb = fixture.CreateDbContext();
        var row = await verifyDb.OutboxEmailDeliveries.AsNoTracking().SingleAsync(d => d.OutboxMessageId == outboxId);
        row.DeliveredAt.Should().BeCloseTo(deliveredAt, TimeSpan.FromSeconds(1));
        row.ClaimToken.Should().BeNull();
        row.ClaimedUntil.Should().BeNull();

        var store2 = CreateStore(verifyDb);
        (DeliveryClaimStatus subsequentStatus, Guid? subsequentToken) = await store2.TryClaimDelivery(
            outboxId,
            messageId,
            recipientAddress,
            recipientUserId,
            EmailTemplateKind.PURCHASE_RECEIPT,
            TimeSpan.FromMinutes(2));

        subsequentStatus.Should().Be(DeliveryClaimStatus.ALREADY_DELIVERED);
        subsequentToken.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseClaim_ShouldAllowImmediateReclaim()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var store = CreateStore(db);

        var outboxId = Guid.NewGuid();
        var messageId = $"<{outboxId:N}@mail.localhost>";
        var recipientUserId = Guid.NewGuid();
        const string recipientAddress = "release@example.com";

        (DeliveryClaimStatus status1, Guid? token1) = await store.TryClaimDelivery(
            outboxId,
            messageId,
            recipientAddress,
            recipientUserId,
            EmailTemplateKind.PURCHASE_RECEIPT,
            TimeSpan.FromMinutes(5));

        status1.Should().Be(DeliveryClaimStatus.CLAIMED);

        var released = await store.ReleaseClaim(outboxId, token1!.Value);
        released.Should().BeTrue();

        await using var db2 = fixture.CreateDbContext();
        var store2 = CreateStore(db2);
        (DeliveryClaimStatus status2, Guid? token2) = await store2.TryClaimDelivery(
            outboxId,
            messageId,
            recipientAddress,
            recipientUserId,
            EmailTemplateKind.PURCHASE_RECEIPT,
            TimeSpan.FromMinutes(5));

        status2.Should().Be(DeliveryClaimStatus.CLAIMED);
        token2.Should().NotBeNull();
        token2.Value.Should().NotBe(token1.Value);
    }

    [Fact]
    public async Task ExpiredClaim_CanBeReclaimed_AndStaleTokenCannotConfirm()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var store = CreateStore(db);

        var outboxId = Guid.NewGuid();
        var messageId = $"<{outboxId:N}@mail.localhost>";
        var recipientUserId = Guid.NewGuid();
        const string recipientAddress = "expired@example.com";

        (DeliveryClaimStatus status1, Guid? token1) = await store.TryClaimDelivery(
            outboxId,
            messageId,
            recipientAddress,
            recipientUserId,
            EmailTemplateKind.PURCHASE_RECEIPT,
            TimeSpan.FromMilliseconds(50));

        status1.Should().Be(DeliveryClaimStatus.CLAIMED);

        await Task.Delay(80);

        await using var db2 = fixture.CreateDbContext();
        var store2 = CreateStore(db2);
        (DeliveryClaimStatus status2, Guid? token2) = await store2.TryClaimDelivery(
            outboxId,
            messageId,
            recipientAddress,
            recipientUserId,
            EmailTemplateKind.PURCHASE_RECEIPT,
            TimeSpan.FromMinutes(5));

        status2.Should().Be(DeliveryClaimStatus.CLAIMED);
        token2!.Value.Should().NotBe(token1!.Value);

        // Stale token confirmation must be rejected
        var staleConfirm = await store.ConfirmDelivery(outboxId, token1.Value, DateTimeOffset.UtcNow);
        staleConfirm.Should().BeFalse();

        // Fresh token confirmation succeeds
        var freshConfirm = await store2.ConfirmDelivery(outboxId, token2.Value, DateTimeOffset.UtcNow);
        freshConfirm.Should().BeTrue();
    }

    [Fact]
    public async Task ExpiredClaim_WhenReclaimedBySecondStoreInstance_FirstStoreConfirmationIsRejected()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var outboxId = Guid.NewGuid();
        var recipientUserId = Guid.NewGuid();
        const string recipientAddress = "blocked-worker@example.com";
        var messageId = $"<{outboxId:N}@mail.localhost>";

        await using var dbA = fixture.CreateDbContext();
        await using var dbB = fixture.CreateDbContext();
        var storeA = CreateStore(dbA);
        var storeB = CreateStore(dbB);

        // Worker A claims with 50ms lease
        (DeliveryClaimStatus statusA, Guid? tokenA) = await storeA.TryClaimDelivery(
            outboxId,
            messageId,
            recipientAddress,
            recipientUserId,
            EmailTemplateKind.PURCHASE_RECEIPT,
            TimeSpan.FromMilliseconds(50));

        statusA.Should().Be(DeliveryClaimStatus.CLAIMED);
        tokenA.Should().NotBeNull();

        // Worker A is blocked on slow SMTP; lease expires
        await Task.Delay(80);

        // Worker B detects expired lease, reclaims with fresh token
        (DeliveryClaimStatus statusB, Guid? tokenB) = await storeB.TryClaimDelivery(
            outboxId,
            messageId,
            recipientAddress,
            recipientUserId,
            EmailTemplateKind.PURCHASE_RECEIPT,
            TimeSpan.FromMinutes(2));

        statusB.Should().Be(DeliveryClaimStatus.CLAIMED);
        tokenB.Should().NotBeNull();
        tokenB.Value.Should().NotBe(tokenA.Value);

        // Worker B finishes SMTP and confirms
        var confirmedB = await storeB.ConfirmDelivery(outboxId, tokenB.Value, DateTimeOffset.UtcNow);
        confirmedB.Should().BeTrue();

        // Worker A eventually unblocks and attempts confirm -> rejected (lost claim)
        var confirmedA = await storeA.ConfirmDelivery(outboxId, tokenA.Value, DateTimeOffset.UtcNow);
        confirmedA.Should().BeFalse();

        // Exactly one delivery record exists and it is marked delivered
        await using var verifyDb = fixture.CreateDbContext();
        var delivery = await verifyDb.OutboxEmailDeliveries.AsNoTracking().SingleAsync(d => d.OutboxMessageId == outboxId);
        delivery.DeliveredAt.Should().NotBeNull();
        delivery.ClaimToken.Should().BeNull();
    }
}
