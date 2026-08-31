using Ardalis.Result;
using AssetBlock.Application.UseCases.Admin.Outbox.ReplayDeadLetter;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.UseCases.Admin.Outbox;

public sealed class ReplayDeadLetterCommandHandlerTests
{
    private readonly IOutboxStore _outboxStore = Substitute.For<IOutboxStore>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();
    private readonly ReplayDeadLetterCommandHandler _handler;

    public ReplayDeadLetterCommandHandlerTests()
    {
        _unitOfWork.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                Func<CancellationToken, Task> action = callInfo.Arg<Func<CancellationToken, Task>>();
                CancellationToken ct = callInfo.Arg<CancellationToken>();
                await action(ct);
            });

        _handler = new ReplayDeadLetterCommandHandler(_outboxStore, _unitOfWork, _auditWriter);
    }

    [Fact]
    public async Task Handle_WhenNotFound_ShouldReturnNotFoundResult()
    {
        var id = Guid.NewGuid();
        _outboxStore.ReplayDeadLetter(id, Arg.Any<CancellationToken>())
            .Returns((OutboxReplayOutcome.NOT_FOUND, (ReplayDeadLetterResponseDto?)null));

        Result<ReplayDeadLetterResponseDto> result = await _handler.Handle(new ReplayDeadLetterCommand(id), CancellationToken.None);

        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_OUTBOX_MESSAGE_NOT_FOUND);
        await _auditWriter.DidNotReceiveWithAnyArgs().Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenNotDeadLettered_ShouldReturnConflictResult()
    {
        var id = Guid.NewGuid();
        _outboxStore.ReplayDeadLetter(id, Arg.Any<CancellationToken>())
            .Returns((OutboxReplayOutcome.NOT_DEAD_LETTERED, (ReplayDeadLetterResponseDto?)null));

        Result<ReplayDeadLetterResponseDto> result = await _handler.Handle(new ReplayDeadLetterCommand(id), CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_OUTBOX_NOT_DEAD_LETTERED);
        await _auditWriter.DidNotReceiveWithAnyArgs().Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenReplaySuccessful_ShouldAuditAndReturnSuccess()
    {
        var id = Guid.NewGuid();
        DateTimeOffset replayedAt = DateTimeOffset.UtcNow;
        var response = new ReplayDeadLetterResponseDto(id, replayedAt, 1);

        _outboxStore.ReplayDeadLetter(id, Arg.Any<CancellationToken>())
            .Returns((OutboxReplayOutcome.SUCCESS, response));

        Result<ReplayDeadLetterResponseDto> result = await _handler.Handle(new ReplayDeadLetterCommand(id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(response);

        await _auditWriter.Received(1).Write(
            Arg.Is<AuditEvent>(a =>
                a.Action == AuditActions.OUTBOX_DEAD_LETTER_REPLAY
                && a.Outcome == AuditOutcome.SUCCESS
                && a.ResourceType == AuditResourceTypes.OUTBOX_MESSAGE
                && a.ResourceId == id.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenAuditWriteThrows_ShouldPropagateExceptionAndNotReturnSuccess()
    {
        var id = Guid.NewGuid();
        var response = new ReplayDeadLetterResponseDto(id, DateTimeOffset.UtcNow, 1);

        _outboxStore.ReplayDeadLetter(id, Arg.Any<CancellationToken>())
            .Returns((OutboxReplayOutcome.SUCCESS, response));
        _auditWriter.Write(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Audit store connection failed."));

        Func<Task<Result<ReplayDeadLetterResponseDto>>> act = () => _handler.Handle(new ReplayDeadLetterCommand(id), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Audit store connection failed.");
    }
}
