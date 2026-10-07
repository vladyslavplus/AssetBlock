using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Application.UseCases.Assets.WithdrawSubmission;

internal sealed class WithdrawSubmissionCommandHandler(
    IModerationFoundationStore moderationFoundationStore)
    : IRequestHandler<WithdrawSubmissionCommand, Result<WithdrawSubmissionResult>>
{
    public async Task<Result<WithdrawSubmissionResult>> Handle(WithdrawSubmissionCommand request, CancellationToken cancellationToken)
    {
        var requestDigest = DraftPayloadJson.ComputeDigest(DraftPayloadJson.Serialize(new
        {
            request.SubmissionId,
            request.ExpectedCaseRevision
        }));

        WithdrawSubmissionResult result = await moderationFoundationStore.WithdrawSubmission(
            new WithdrawSubmissionRequest(
                request.OwnerId,
                request.SubmissionId,
                request.ExpectedCaseRevision,
                request.OperationId,
                requestDigest,
                ModerationWithdrawalReason.SELLER_WITHDRAWAL,
                // The store writes this inside the mutation transaction; replays never re-write it.
                new AuditEvent(
                    AuditActions.ASSET_SUBMISSION_WITHDRAW,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.ASSET,
                    request.SubmissionId.ToString())),
            cancellationToken);

        return result.Status switch
        {
            WithdrawSubmissionStatus.SUCCEEDED => Result.Success(result),
            WithdrawSubmissionStatus.NOT_FOUND => Result.NotFound(ErrorCodes.ERR_MODERATION_CASE_NOT_FOUND),
            WithdrawSubmissionStatus.FORBIDDEN => Result.Forbidden(ErrorCodes.ERR_FORBIDDEN),
            WithdrawSubmissionStatus.STALE_CASE or WithdrawSubmissionStatus.NOT_ACTIVE
                => Result.Conflict(ErrorCodes.ERR_SUBMISSION_WITHDRAW_CONFLICT),
            _ => Result.Conflict(ErrorCodes.ERR_MODERATION_OPERATION_CONFLICT)
        };
    }
}
