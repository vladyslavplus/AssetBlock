using AssetBlock.Application.UseCases.Reviews.DeleteReview;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
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
    public async Task Handle_WhenExceptionThrown_ShouldLogSafeContextAndRethrow()
    {
        var testLogger = new TestLogger<DeleteReviewCommandHandler>();
        var unitOfWorkMock = Substitute.For<IUnitOfWork>();
        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        var handler = new DeleteReviewCommandHandler(
            _reviewStoreMock,
            unitOfWorkMock,
            _auditWriterMock,
            _cacheMock,
            testLogger);

        var command = new DeleteReviewCommand(Guid.NewGuid());
        var review = new Review { Id = command.Id, AssetId = Guid.NewGuid(), UserId = Guid.NewGuid(), Rating = 5 };
        _reviewStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns(review);
        _reviewStoreMock.Delete(command.Id, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("DB Error"));

        var act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("DB Error");
        testLogger.Logs.Should().Contain(l =>
            l.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && l.Message.Contains(command.Id.ToString())
            && l.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Handle_WhenReviewLookupThrows_ShouldLogSafeContextAndRethrow()
    {
        var testLogger = new TestLogger<DeleteReviewCommandHandler>();
        var unitOfWorkMock = Substitute.For<IUnitOfWork>();
        var handler = new DeleteReviewCommandHandler(
            _reviewStoreMock,
            unitOfWorkMock,
            _auditWriterMock,
            _cacheMock,
            testLogger);

        var command = new DeleteReviewCommand(Guid.NewGuid());
        _reviewStoreMock.GetById(command.Id, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB Error on lookup"));

        var act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("DB Error on lookup");
        testLogger.Logs.Should().ContainSingle(l =>
            l.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && l.Message.Contains(command.Id.ToString())
            && l.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Handle_WhenReviewLookupCancelled_ShouldRethrowWithoutErrorLogging()
    {
        var testLogger = new TestLogger<DeleteReviewCommandHandler>();
        var unitOfWorkMock = Substitute.For<IUnitOfWork>();
        var handler = new DeleteReviewCommandHandler(
            _reviewStoreMock,
            unitOfWorkMock,
            _auditWriterMock,
            _cacheMock,
            testLogger);

        var command = new DeleteReviewCommand(Guid.NewGuid());
        _reviewStoreMock.GetById(command.Id, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        testLogger.Logs.Should().NotContain(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task Handle_WhenCancelled_ShouldRethrowWithoutErrorLogging()
    {
        var testLogger = new TestLogger<DeleteReviewCommandHandler>();
        var unitOfWorkMock = Substitute.For<IUnitOfWork>();
        unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        var handler = new DeleteReviewCommandHandler(
            _reviewStoreMock,
            unitOfWorkMock,
            _auditWriterMock,
            _cacheMock,
            testLogger);

        var command = new DeleteReviewCommand(Guid.NewGuid());
        var review = new Review { Id = command.Id, AssetId = Guid.NewGuid(), UserId = Guid.NewGuid(), Rating = 5 };
        _reviewStoreMock.GetById(command.Id, Arg.Any<CancellationToken>()).Returns(review);
        _reviewStoreMock.Delete(command.Id, Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());

        var act = () => handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        testLogger.Logs.Should().NotContain(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    private sealed class TestLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)> Logs { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
