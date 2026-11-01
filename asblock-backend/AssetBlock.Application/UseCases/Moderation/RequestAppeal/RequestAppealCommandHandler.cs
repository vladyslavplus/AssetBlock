using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;

namespace AssetBlock.Application.UseCases.Moderation.RequestAppeal;

internal sealed class RequestAppealCommandHandler(IModerationFoundationStore store) : IRequestHandler<RequestAppealCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(RequestAppealCommand request, CancellationToken cancellationToken)
    {
        var rationale = request.Rationale!.Trim();
        var digest = DraftPayloadJson.ComputeDigest(DraftPayloadJson.Serialize(new
        {
            request.OwnerId,
            request.AssetId,
            request.AssetVersionId,
            request.WorkspaceId,
            request.OperationId,
            request.ExpectedWorkspaceRevision,
            request.ExpectedCaseRevision,
            Rationale = rationale
        }));
        GuardedSubmissionResult result = await store.RequestAppeal(new AppealCreateRequest(request.OwnerId, request.AssetId,
            request.AssetVersionId, request.WorkspaceId, request.ExpectedWorkspaceRevision, request.ExpectedCaseRevision,
            request.OperationId, rationale, digest), cancellationToken);
        return result.Status switch
        {
            GuardedSubmissionStatus.CREATED or GuardedSubmissionStatus.REPLAYED when result.SubmissionId.HasValue => Result.Success(result.SubmissionId.Value),
            _ when result.BlockedReasonCode == ErrorCodes.ERR_FORBIDDEN => Result.Forbidden(ErrorCodes.ERR_FORBIDDEN),
            _ when result.BlockedReasonCode == ErrorCodes.ERR_REVIEW_AUDIT_UNAVAILABLE => Result.Unavailable(ErrorCodes.ERR_REVIEW_AUDIT_UNAVAILABLE),
            _ => Result.Conflict(result.BlockedReasonCode ?? ErrorCodes.ERR_MODERATION_APPEAL_NOT_ALLOWED)
        };
    }
}
