using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Application.UseCases.Admin.Outbox.ReplayDeadLetter;

internal sealed class ReplayDeadLetterCommandHandler(
    IOutboxStore outboxStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter) : IRequestHandler<ReplayDeadLetterCommand, Result<ReplayDeadLetterResponseDto>>
{
    public async Task<Result<ReplayDeadLetterResponseDto>> Handle(
        ReplayDeadLetterCommand request,
        CancellationToken cancellationToken)
    {
        OutboxReplayOutcome outcome = OutboxReplayOutcome.NOT_FOUND;
        ReplayDeadLetterResponseDto? response = null;

        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            (outcome, response) = await outboxStore.ReplayDeadLetter(request.Id, ct);
            if (outcome == OutboxReplayOutcome.SUCCESS)
            {
                await auditWriter.Write(new AuditEvent(
                    AuditActions.OUTBOX_DEAD_LETTER_REPLAY,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.OUTBOX_MESSAGE,
                    request.Id.ToString()), ct);
            }
        }, cancellationToken);

        if (outcome == OutboxReplayOutcome.NOT_FOUND)
        {
            return Result.NotFound(ErrorCodes.ERR_OUTBOX_MESSAGE_NOT_FOUND);
        }

        if (outcome == OutboxReplayOutcome.NOT_DEAD_LETTERED)
        {
            return Result.Conflict(ErrorCodes.ERR_OUTBOX_NOT_DEAD_LETTERED);
        }

        return Result.Success(response!);
    }
}
