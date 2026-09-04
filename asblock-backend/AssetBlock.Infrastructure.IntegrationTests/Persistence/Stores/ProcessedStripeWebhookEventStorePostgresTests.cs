using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class ProcessedStripeWebhookEventStorePostgresTests(PostgresFixture fixture)
{
    private static ProcessedStripeWebhookEventStore CreateStore(ApplicationDbContext db) =>
        new(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessedStripeWebhookEventStore>.Instance);

    [Fact]
    public async Task TryRecordEvent_WhenNewEvent_InsertsRowAndReturnsTrue()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ProcessedStripeWebhookEventStore store = CreateStore(db);
        var eventId = $"evt_new_{Guid.NewGuid():N}";
        const string eventType = "checkout.session.completed";
        DateTimeOffset processedAt = DateTimeOffset.UtcNow;

        var recorded = await store.TryRecordEvent(eventId, eventType, processedAt, CancellationToken.None);

        recorded.Should().BeTrue();

        ProcessedStripeWebhookEvent? entity = await db.ProcessedStripeWebhookEvents
            .SingleOrDefaultAsync(e => e.StripeEventId == eventId);
        entity.Should().NotBeNull();
        entity.EventType.Should().Be(eventType);
        entity.ProcessedAt.Should().BeCloseTo(processedAt, TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task TryRecordEvent_WhenDuplicateEvent_ReturnsFalseWithoutAbortingTransaction()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ProcessedStripeWebhookEventStore store = CreateStore(db);
        var eventId = $"evt_dup_{Guid.NewGuid():N}";
        const string eventType = "checkout.session.completed";
        DateTimeOffset processedAt = DateTimeOffset.UtcNow;

        var first = await store.TryRecordEvent(eventId, eventType, processedAt, CancellationToken.None);
        first.Should().BeTrue();

        // In the same or separate transaction, duplicate claim returns false
        var unitOfWork = new EfUnitOfWork(db);
        var secondInsideTx = false;
        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            secondInsideTx = await store.TryRecordEvent(eventId, eventType, processedAt, ct);
        });

        secondInsideTx.Should().BeFalse();

        var totalCount = await db.ProcessedStripeWebhookEvents.CountAsync(e => e.StripeEventId == eventId);
        totalCount.Should().Be(1);
    }

    [Fact]
    public async Task TryRecordEvent_WhenTransactionFails_RollsBackLedgerEntry()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ProcessedStripeWebhookEventStore store = CreateStore(db);
        var eventId = $"evt_rollback_{Guid.NewGuid():N}";
        const string eventType = "checkout.session.completed";
        DateTimeOffset processedAt = DateTimeOffset.UtcNow;
        var unitOfWork = new EfUnitOfWork(db);

        Func<Task> failingAction = () => unitOfWork.ExecuteInTransaction(async ct =>
        {
            var recorded = await store.TryRecordEvent(eventId, eventType, processedAt, ct);
            recorded.Should().BeTrue();
            throw new InvalidOperationException("Simulated failure inside transaction");
        });

        await failingAction.Should().ThrowAsync<InvalidOperationException>();

        var exists = await db.ProcessedStripeWebhookEvents.AnyAsync(e => e.StripeEventId == eventId);
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task DirectEfInsert_WhenDuplicateStripeEventId_ThrowsDbUpdateException()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var eventId = $"evt_constraint_{Guid.NewGuid():N}";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        db.ProcessedStripeWebhookEvents.Add(new ProcessedStripeWebhookEvent
        {
            Id = Guid.NewGuid(),
            StripeEventId = eventId,
            EventType = "checkout.session.completed",
            ProcessedAt = now
        });
        await db.SaveChangesAsync();

        db.ProcessedStripeWebhookEvents.Add(new ProcessedStripeWebhookEvent
        {
            Id = Guid.NewGuid(),
            StripeEventId = eventId,
            EventType = "checkout.session.completed",
            ProcessedAt = now
        });

        Func<Task> act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
