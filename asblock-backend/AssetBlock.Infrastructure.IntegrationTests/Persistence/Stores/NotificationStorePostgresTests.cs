using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class NotificationStorePostgresTests(PostgresFixture fixture)
{
    private static NotificationStore CreateStore(ApplicationDbContext db) =>
        new(db, NullLogger<NotificationStore>.Instance);

    [Fact]
    public async Task MarkAllRead_WhenNothingUnread_ShouldReturnZero()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var user = TestData.CreateUser("notify-user-1", "notify-user-1@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var alreadyReadAt = DateTimeOffset.UtcNow.AddHours(-1);
        db.UserNotifications.Add(CreateNotification(user.Id, readAt: alreadyReadAt));
        await db.SaveChangesAsync();

        var sut = CreateStore(db);

        (await sut.MarkAllRead(user.Id)).Should().Be(0);
    }

    [Fact]
    public async Task MarkAllRead_WhenUnreadExist_ShouldSetReadAtAndReturnCount()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var user = TestData.CreateUser("notify-user-2", "notify-user-2@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var unreadA = CreateNotification(user.Id);
        var unreadB = CreateNotification(user.Id);
        var alreadyReadAt = DateTimeOffset.UtcNow.AddHours(-2);
        var alreadyRead = CreateNotification(user.Id, readAt: alreadyReadAt);
        db.UserNotifications.AddRange(unreadA, unreadB, alreadyRead);
        await db.SaveChangesAsync();

        await db.Entry(alreadyRead).ReloadAsync();
        var expectedAlreadyReadAt = alreadyRead.ReadAt;

        var sut = CreateStore(db);

        var affected = await sut.MarkAllRead(user.Id);

        affected.Should().Be(2);

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.UserNotifications
            .AsNoTracking()
            .Where(n => n.RecipientUserId == user.Id)
            .ToDictionaryAsync(n => n.Id);

        rows.Should().HaveCount(3);
        rows[unreadA.Id].ReadAt.Should().NotBeNull();
        rows[unreadB.Id].ReadAt.Should().NotBeNull();
        rows[alreadyRead.Id].ReadAt.Should().Be(expectedAlreadyReadAt);

        var verifyStore = CreateStore(verify);
        var unreadPage = await verifyStore.GetPaged(user.Id, new GetNotificationsRequest { UnreadOnly = true });
        unreadPage.Items.Should().BeEmpty();
        (await verifyStore.MarkAllRead(user.Id)).Should().Be(0);
    }

    private static UserNotification CreateNotification(Guid recipientUserId, DateTimeOffset? readAt = null)
    {
        return new UserNotification
        {
            Id = Guid.NewGuid(),
            RecipientUserId = recipientUserId,
            Kind = NotificationKind.REVIEW_RECEIVED,
            MetadataJson = "{}",
            ReadAt = readAt,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
