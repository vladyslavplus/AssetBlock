using AssetBlock.Application.UseCases.Users.ListNotifications;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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
        var n = new UserNotification
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
            .Returns(new PagedResult<UserNotification>([n], 1, 1, 10));

        var result = await _handler.Handle(new GetNotificationsQuery(userId, request), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().ContainSingle();
        var item = result.Value.Items[0];
        item.Id.Should().Be(n.Id);
        item.Kind.Should().Be(nameof(NotificationKind.REVIEW_RECEIVED));
        item.MetadataJson.Should().Be(n.MetadataJson);
        item.ReadAt.Should().BeNull();
    }
}
