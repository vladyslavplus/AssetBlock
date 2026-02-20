using AssetBlock.Application.UseCases.Reviews.DeleteReview;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Reviews;

public class DeleteReviewCommandHandlerTests
{
    private readonly IReviewStore _reviewStoreMock;
    private readonly ICacheService _cacheMock;
    private readonly DeleteReviewCommandHandler _handler;

    public DeleteReviewCommandHandlerTests()
    {
        _reviewStoreMock = Substitute.For<IReviewStore>();
        _cacheMock = Substitute.For<ICacheService>();

        _handler = new DeleteReviewCommandHandler(
            _reviewStoreMock,
            _cacheMock,
            NullLogger<DeleteReviewCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenReviewNotFound_ShouldReturnError()
    {
        // Arrange
        var command = new DeleteReviewCommand(Guid.NewGuid());
        _reviewStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns((Review?)null);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_REVIEW_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenNotDeleted_ShouldReturnError()
    {
        // Arrange
        var command = new DeleteReviewCommand(Guid.NewGuid());
        var review = new Review { Id = command.Id, AssetId = Guid.NewGuid(), UserId = Guid.NewGuid(), Rating = 5 };
        _reviewStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns(review);
        _reviewStoreMock.Delete(command.Id, Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_REVIEW_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenSuccessful_ShouldReturnSuccessAndInvalidateCache()
    {
        // Arrange
        var command = new DeleteReviewCommand(Guid.NewGuid());
        var review = new Review { Id = command.Id, AssetId = Guid.NewGuid(), UserId = Guid.NewGuid(), Rating = 5 };
        _reviewStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns(review);
        _reviewStoreMock.Delete(command.Id, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await _cacheMock.Received().RemoveByPrefix(Arg.Is<string>(s => s.StartsWith(CacheKeys.REVIEWS_LIST_PREFIX)), Arg.Any<CancellationToken>());
        await _cacheMock.Received().RemoveByPrefix(CacheKeys.ReviewItem(command.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenExceptionThrown_ShouldReturnBadRequest()
    {
        // Arrange
        var command = new DeleteReviewCommand(Guid.NewGuid());
        var review = new Review { Id = command.Id, AssetId = Guid.NewGuid(), UserId = Guid.NewGuid(), Rating = 5 };
        _reviewStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns(review);
        _reviewStoreMock.Delete(command.Id, Arg.Any<CancellationToken>()).ThrowsAsync(new Exception("DB Error"));

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_BAD_REQUEST);
    }
}
