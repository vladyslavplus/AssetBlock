using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBlock.Infrastructure.Tests.Persistence.Stores;

public sealed class NotificationStoreTests
{
    [Fact]
    public async Task Add_GetPaged_MarkRead()
    {
        await using var db = InMemoryDbContextFactory.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Username = "u",
            Email = "u@u.com",
            PasswordHash = "h",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = new NotificationStore(db, NullLogger<NotificationStore>.Instance);
        var n = new UserNotification
        {
            Id = Guid.NewGuid(),
            RecipientUserId = userId,
            Kind = NotificationKind.REVIEW_RECEIVED,
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await sut.Add(n);

        var page = await sut.GetPaged(userId, new GetNotificationsRequest { Page = 1, PageSize = 10, SortBy = "ReadAt" });
        page.Items.Should().Contain(x => x.Id == n.Id);

        var unread = await sut.GetPaged(userId, new GetNotificationsRequest { UnreadOnly = true });
        unread.Items.Should().HaveCount(1);

        (await sut.MarkRead(userId, n.Id)).Should().BeTrue();
        (await sut.MarkRead(userId, n.Id)).Should().BeTrue();

        var after = await sut.GetPaged(userId, new GetNotificationsRequest { UnreadOnly = true });
        after.Items.Should().BeEmpty();

        (await sut.MarkRead(userId, Guid.NewGuid())).Should().BeFalse();
    }
}
