using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Moderation;
namespace AssetBlock.Application.UseCases.Assets.SaveSellerEvidence;

internal sealed class SaveSellerEvidenceCommandHandler(IModerationFoundationStore store) : IRequestHandler<SaveSellerEvidenceCommand, Result<SellerEvidenceSavedDto>>
{
    public async Task<Result<SellerEvidenceSavedDto>> Handle(SaveSellerEvidenceCommand request, CancellationToken cancellationToken)
    {
        var text = request.Text.Trim();
        var digest = DraftPayloadJson.ComputeDigest(DraftPayloadJson.Serialize(new
        {
            request.AssetId,
            request.AssetVersionId,
            request.WorkspaceId,
            request.ReportId,
            request.FindingId,
            request.ResponseType,
            Text = text,
            request.ExpectedWorkspaceRevision,
            request.ExpectedEvidenceRevision
        }));
        SellerEvidenceWriteResult result = await store.SaveSellerEvidence(new SellerEvidenceWriteRequest(request.OwnerId,
            request.AssetId, request.AssetVersionId, request.WorkspaceId, request.ReportId, request.FindingId, request.ResponseType,
            text, request.ExpectedWorkspaceRevision, request.ExpectedEvidenceRevision, request.OperationId, digest), cancellationToken);
        return result.Status switch
        {
            SellerEvidenceWriteStatus.SAVED => Result.Success(result.Saved!),
            SellerEvidenceWriteStatus.STALE_EVIDENCE => Result.Conflict(ErrorCodes.ERR_SELLER_EVIDENCE_STALE),
            SellerEvidenceWriteStatus.STALE_WORKSPACE => Result.Conflict(ErrorCodes.ERR_MODERATION_WORKSPACE_STALE),
            SellerEvidenceWriteStatus.OPERATION_CONFLICT => Result.Conflict(ErrorCodes.ERR_MODERATION_OPERATION_CONFLICT),
            SellerEvidenceWriteStatus.ANALYSIS_UNAVAILABLE => Result.Conflict(ErrorCodes.ERR_ANALYSIS_NOT_AVAILABLE),
            SellerEvidenceWriteStatus.INVALID_FINDING => Result.NotFound(ErrorCodes.ERR_RESPONSE_FINDING_NOT_AVAILABLE),
            SellerEvidenceWriteStatus.NOT_FOUND => Result.NotFound(ErrorCodes.ERR_ASSET_VERSION_NOT_FOUND),
            SellerEvidenceWriteStatus.INVALID_INPUT => ResultError.Error<SellerEvidenceSavedDto>(ErrorCodes.ERR_SELLER_RESPONSE_INVALID),
            _ => Result.Forbidden(ErrorCodes.ERR_FORBIDDEN)
        };
    }
}
