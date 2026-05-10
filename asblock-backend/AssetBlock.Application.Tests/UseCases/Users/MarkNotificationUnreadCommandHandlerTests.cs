using Ardalis.Result;
using AssetBlock.Application.UseCases.Users.MarkNotificationUnread;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Users;

public sealed class MarkNotificationUnreadCommandHandlerTests
{
    private readonly INotificationStore _storeMock;
    private readonly MarkNotificationUnreadCommandHandler _handler;

    public MarkNotificationUnreadCommandHandlerTests()
    {
        _storeMock = Substitute.For<INotificationStore>();
        _handler = new MarkNotificationUnreadCommandHandler(_storeMock, NullLogger<MarkNotificationUnreadCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenNotFound_ShouldReturnNotFound()
    {
        var userId = Guid.NewGuid();
        var id = Guid.NewGuid();
        _storeMock.MarkUnread(userId, id, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _handler.Handle(new MarkNotificationUnreadCommand(userId, id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_NOTIFICATION_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenFound_ShouldSucceed()
    {
        var userId = Guid.NewGuid();
        var id = Guid.NewGuid();
        _storeMock.MarkUnread(userId, id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _handler.Handle(new MarkNotificationUnreadCommand(userId, id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}
