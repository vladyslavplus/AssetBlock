using AssetBlock.Application.UseCases.Users.ListNotifications;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Users;

public class GetNotificationsQueryHandlerTests
{
    private readonly INotificationStore _storeMock;
    private readonly GetNotificationsQueryHandler _handler;

    public GetNotificationsQueryHandlerTests()
    {
        _storeMock = Substitute.For<INotificationStore>();
        _handler = new GetNotificationsQueryHandler(_storeMock, NullLogger<GetNotificationsQueryHandler>.Instance);
    }

    [Fact]
    public async Task Handle_ShouldMapToDtos()
    {
        var userId = Guid.NewGuid();
        var notification = new UserNotification
        {
            Id = Guid.NewGuid(),
            RecipientUserId = userId,
            Kind = NotificationKind.REVIEW_RECEIVED,
            MetadataJson = """{"assetId":"...","assetTitle":"T"}""",
            CreatedAt = DateTimeOffset.UtcNow,
            ReadAt = null
        };
        var request = new GetNotificationsRequest { Page = 1, PageSize = 10 };
        _storeMock.GetPaged(userId, request, Arg.Any<CancellationToken>())
            .Returns(new PagedResult<UserNotification>([notification], 1, 1, 10));

        var result = await _handler.Handle(new GetNotificationsQuery(userId, request), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _storeMock.Received(1).GetPaged(userId, request, Arg.Any<CancellationToken>());
        result.Value!.Items.Should().ContainSingle();
        var item = result.Value.Items[0];
        item.Id.Should().Be(notification.Id);
        item.Kind.Should().Be(nameof(NotificationKind.REVIEW_RECEIVED));
        item.MetadataJson.Should().Be(notification.MetadataJson);
        item.ReadAt.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenStoreThrows_ShouldReturnListFailedError()
    {
        var userId = Guid.NewGuid();
        var request = new GetNotificationsRequest { Page = 1, PageSize = 10 };
        _storeMock.GetPaged(userId, request, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Database error"));

        var result = await _handler.Handle(new GetNotificationsQuery(userId, request), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(Ardalis.Result.ResultStatus.Invalid);
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_NOTIFICATIONS_LIST_FAILED);
    }

    [Fact]
    public async Task Handle_ShouldReturnEmpty_WhenNoNotifications()
    {
        var userId = Guid.NewGuid();
        var request = new GetNotificationsRequest { Page = 1, PageSize = 10 };
        _storeMock.GetPaged(userId, request, Arg.Any<CancellationToken>())
            .Returns(new PagedResult<UserNotification>([], 0, 1, 10));

        var result = await _handler.Handle(new GetNotificationsQuery(userId, request), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Items.Should().BeEmpty();
        result.Value.TotalCount.Should().Be(0);
        result.Value.Page.Should().Be(1);
        result.Value.PageSize.Should().Be(10);
    }

    [Fact]
    public async Task Handle_ShouldHandlePagination_ForMultipleNotifications()
    {
        var userId = Guid.NewGuid();
        var first = new UserNotification
        {
            Id = Guid.NewGuid(),
            RecipientUserId = userId,
            Kind = NotificationKind.PURCHASE_COMPLETED,
            MetadataJson = """{"assetId":"a1"}""",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            ReadAt = null
        };
        var second = new UserNotification
        {
            Id = Guid.NewGuid(),
            RecipientUserId = userId,
            Kind = NotificationKind.DOWNLOAD_READY,
            MetadataJson = """{"assetId":"a2"}""",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ReadAt = null
        };
        var request = new GetNotificationsRequest { Page = 2, PageSize = 2 };
        _storeMock.GetPaged(userId, request, Arg.Any<CancellationToken>())
            .Returns(new PagedResult<UserNotification>([first, second], 5, 2, 2));

        var result = await _handler.Handle(new GetNotificationsQuery(userId, request), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Items.Should().HaveCount(2);
        result.Value.Items[0].Id.Should().Be(first.Id);
        result.Value.Items[1].Id.Should().Be(second.Id);
        result.Value.Items[0].Kind.Should().Be(nameof(NotificationKind.PURCHASE_COMPLETED));
        result.Value.Items[1].Kind.Should().Be(nameof(NotificationKind.DOWNLOAD_READY));
        result.Value.TotalCount.Should().Be(5);
        result.Value.Page.Should().Be(2);
        result.Value.PageSize.Should().Be(2);
    }

    [Fact]
    public async Task Handle_ShouldMapReadNotification_WhenReadAtPopulated()
    {
        var userId = Guid.NewGuid();
        var readAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notification = new UserNotification
        {
            Id = Guid.NewGuid(),
            RecipientUserId = userId,
            Kind = NotificationKind.ASSET_SOLD,
            MetadataJson = """{"assetId":"asset-42"}""",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            ReadAt = readAt
        };
        var request = new GetNotificationsRequest { Page = 1, PageSize = 10 };
        _storeMock.GetPaged(userId, request, Arg.Any<CancellationToken>())
            .Returns(new PagedResult<UserNotification>([notification], 1, 1, 10));

        var result = await _handler.Handle(new GetNotificationsQuery(userId, request), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Items.Should().ContainSingle();
        var item = result.Value.Items[0];
        item.Id.Should().Be(notification.Id);
        item.Kind.Should().Be(nameof(NotificationKind.ASSET_SOLD));
        item.MetadataJson.Should().Be(notification.MetadataJson);
        item.ReadAt.Should().Be(readAt);
    }

    [Fact]
    public async Task Handle_ShouldMapDifferentNotificationKinds()
    {
        var userId = Guid.NewGuid();
        var notifications = new List<UserNotification>
        {
            new()
            {
                Id = Guid.NewGuid(),
                RecipientUserId = userId,
                Kind = NotificationKind.PURCHASE_COMPLETED,
                MetadataJson = """{"type":"purchase"}""",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
                ReadAt = null
            },
            new()
            {
                Id = Guid.NewGuid(),
                RecipientUserId = userId,
                Kind = NotificationKind.DOWNLOAD_READY,
                MetadataJson = """{"type":"download"}""",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                ReadAt = null
            },
            new()
            {
                Id = Guid.NewGuid(),
                RecipientUserId = userId,
                Kind = NotificationKind.ASSET_SOLD,
                MetadataJson = """{"type":"sold"}""",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                ReadAt = null
            },
            new()
            {
                Id = Guid.NewGuid(),
                RecipientUserId = userId,
                Kind = NotificationKind.REVIEW_RECEIVED,
                MetadataJson = """{"type":"review"}""",
                CreatedAt = DateTimeOffset.UtcNow,
                ReadAt = null
            }
        };
        var request = new GetNotificationsRequest { Page = 1, PageSize = 10 };
        _storeMock.GetPaged(userId, request, Arg.Any<CancellationToken>())
            .Returns(new PagedResult<UserNotification>(notifications, notifications.Count, 1, 10));

        var result = await _handler.Handle(new GetNotificationsQuery(userId, request), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Items.Should().HaveCount(4);
        result.Value.Items.Select(i => i.Kind).Should().Equal(
            nameof(NotificationKind.PURCHASE_COMPLETED),
            nameof(NotificationKind.DOWNLOAD_READY),
            nameof(NotificationKind.ASSET_SOLD),
            nameof(NotificationKind.REVIEW_RECEIVED));
        result.Value.Items[0].MetadataJson.Should().Be("""{"type":"purchase"}""");
        result.Value.Items[1].MetadataJson.Should().Be("""{"type":"download"}""");
        result.Value.Items[2].MetadataJson.Should().Be("""{"type":"sold"}""");
        result.Value.Items[3].MetadataJson.Should().Be("""{"type":"review"}""");
    }
}
