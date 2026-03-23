using Ardalis.Result;
using AssetBlock.Application.UseCases.Users.MarkNotificationRead;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Users;

public class MarkNotificationReadCommandHandlerTests
{
    private readonly INotificationStore _storeMock;
    private readonly MarkNotificationReadCommandHandler _handler;

    public MarkNotificationReadCommandHandlerTests()
    {
        _storeMock = Substitute.For<INotificationStore>();
        _handler = new MarkNotificationReadCommandHandler(_storeMock, NullLogger<MarkNotificationReadCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenNotFound_ShouldReturnNotFound()
    {
        var userId = Guid.NewGuid();
        var id = Guid.NewGuid();
        _storeMock.MarkRead(userId, id, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _handler.Handle(new MarkNotificationReadCommand(userId, id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_NOTIFICATION_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenFound_ShouldSucceed()
    {
        var userId = Guid.NewGuid();
        var id = Guid.NewGuid();
        _storeMock.MarkRead(userId, id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _handler.Handle(new MarkNotificationReadCommand(userId, id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }
}
