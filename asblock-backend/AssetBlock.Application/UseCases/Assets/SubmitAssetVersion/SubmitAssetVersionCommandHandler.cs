using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Moderation;

namespace AssetBlock.Application.UseCases.Assets.SubmitAssetVersion;

internal sealed class SubmitAssetVersionCommandHandler(
    IModerationFoundationStore moderationFoundationStore,
    ICodeAnalysisSellerStore analysisStore)
    : IRequestHandler<SubmitAssetVersionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(SubmitAssetVersionCommand request, CancellationToken cancellationToken)
    {
        var requestDigest = DraftPayloadJson.ComputeDigest(DraftPayloadJson.Serialize(new
        {
            request.AssetId,
            request.AssetVersionId,
            request.WorkspaceId,
            request.ExpectedWorkspaceRevision,
            request.ExpectedCaseRevision
        }));

        SellerAnalysisSummaryDto? analysis = await analysisStore.GetOwnerSummary(request.AssetVersionId, request.OwnerId, cancellationToken);
        GuardedSubmissionResult attempt = await moderationFoundationStore.AttemptGuardedSubmission(
            new GuardedSubmissionRequest(
                request.OwnerId,
                request.AssetId,
                request.AssetVersionId,
                request.WorkspaceId,
                request.ExpectedWorkspaceRevision,
                request.ExpectedCaseRevision,
                request.OperationId,
                requestDigest,
                CodeAnalysisReportHeaderId: analysis is { ReportIsCurrent: true } ? analysis.ReportId : null),
            cancellationToken);

        return attempt.Status switch
        {
            GuardedSubmissionStatus.CREATED or GuardedSubmissionStatus.REPLAYED
                when attempt.SubmissionId.HasValue => Result.Success(attempt.SubmissionId.Value),
            GuardedSubmissionStatus.CONFLICT => Result.Conflict(
                attempt.BlockedReasonCode ?? ErrorCodes.ERR_MODERATION_WORKSPACE_STALE),
            _ => Result.Conflict(attempt.BlockedReasonCode ?? ErrorCodes.ERR_MODERATION_SUBMISSION_BLOCKED)
        };
    }
}
