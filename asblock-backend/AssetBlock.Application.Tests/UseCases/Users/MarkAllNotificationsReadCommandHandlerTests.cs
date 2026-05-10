using Ardalis.Result;
using AssetBlock.Application.UseCases.Users.MarkAllNotificationsRead;
using AssetBlock.Domain.Abstractions.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Users;

public sealed class MarkAllNotificationsReadCommandHandlerTests
{
    private readonly INotificationStore _storeMock;
    private readonly MarkAllNotificationsReadCommandHandler _handler;

    public MarkAllNotificationsReadCommandHandlerTests()
    {
        _storeMock = Substitute.For<INotificationStore>();
        _handler = new MarkAllNotificationsReadCommandHandler(_storeMock, NullLogger<MarkAllNotificationsReadCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_ShouldReturnUpdatedCount()
    {
        var userId = Guid.NewGuid();
        _storeMock.MarkAllRead(userId, Arg.Any<CancellationToken>()).Returns(5);

        var result = await _handler.Handle(new MarkAllNotificationsReadCommand(userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.UpdatedCount.Should().Be(5);
    }
}
