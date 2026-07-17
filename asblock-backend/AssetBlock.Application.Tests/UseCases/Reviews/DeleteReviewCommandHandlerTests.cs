using AssetBlock.Application.UseCases.Reviews.DeleteReview;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Reviews;

public class DeleteReviewCommandHandlerTests
{
    private readonly IReviewStore _reviewStoreMock;
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly ICacheService _cacheMock;
    private readonly DeleteReviewCommandHandler _handler;

    public DeleteReviewCommandHandlerTests()
    {
        _reviewStoreMock = Substitute.For<IReviewStore>();
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();
        _cacheMock = Substitute.For<ICacheService>();

        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new DeleteReviewCommandHandler(
            _reviewStoreMock,
            _unitOfWorkMock,
            _auditWriterMock,
            _cacheMock,
            NullLogger<DeleteReviewCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenReviewNotFound_ShouldReturnNotFound()
    {
        var command = new DeleteReviewCommand(Guid.NewGuid());
        _reviewStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns((Review?)null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(Ardalis.Result.ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_REVIEW_NOT_FOUND);
        await _reviewStoreMock.DidNotReceive().Delete(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenReviewDisappearsBeforeDelete_ShouldReturnNotFoundWithoutAuditOrCacheInvalidation()
    {
        var command = new DeleteReviewCommand(Guid.NewGuid());
        var review = new Review
        {
            Id = command.Id,
            AssetId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Rating = 5
        };
        _reviewStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns(review);
        _reviewStoreMock.Delete(command.Id, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Status.Should().Be(Ardalis.Result.ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_REVIEW_NOT_FOUND);
        await _auditWriterMock.DidNotReceive().Write(
            Arg.Any<AuditEvent>(),
            Arg.Any<CancellationToken>());
        await _cacheMock.DidNotReceive().RemoveByPrefix(
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSuccessful_ShouldWriteAuditAndInvalidateCache()
    {
        var command = new DeleteReviewCommand(Guid.NewGuid());
        var review = new Review { Id = command.Id, AssetId = Guid.NewGuid(), UserId = Guid.NewGuid(), Rating = 5 };
        _reviewStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns(review);
        _reviewStoreMock.Delete(command.Id, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _unitOfWorkMock.Received(1).ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.REVIEW_DELETE &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceId == command.Id.ToString()),
            Arg.Any<CancellationToken>());
        await _cacheMock.Received().RemoveByPrefix(Arg.Is<string>(s => s.StartsWith(CacheKeys.REVIEWS_LIST_PREFIX)), Arg.Any<CancellationToken>());
        await _cacheMock.Received().RemoveByPrefix(CacheKeys.ReviewItem(command.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenExceptionThrown_ShouldReturnInternalError()
    {
        var command = new DeleteReviewCommand(Guid.NewGuid());
        var review = new Review { Id = command.Id, AssetId = Guid.NewGuid(), UserId = Guid.NewGuid(), Rating = 5 };
        _reviewStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns(review);
        _reviewStoreMock.Delete(command.Id, Arg.Any<CancellationToken>()).ThrowsAsync(new Exception("DB Error"));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(Ardalis.Result.ResultStatus.Error);
        result.Errors.Should().Contain(ErrorCodes.ERR_INTERNAL);
    }
}
