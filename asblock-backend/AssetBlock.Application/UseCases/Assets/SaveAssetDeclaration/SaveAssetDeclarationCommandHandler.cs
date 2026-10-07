using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Application.UseCases.Assets.SaveAssetDeclaration;

internal sealed class SaveAssetDeclarationCommandHandler(
    IModerationFoundationStore moderationFoundationStore)
    : IRequestHandler<SaveAssetDeclarationCommand, Result<DraftSaveResult>>
{
    public async Task<Result<DraftSaveResult>> Handle(SaveAssetDeclarationCommand request, CancellationToken cancellationToken)
    {
        // Oversize aggregate declaration input is rejected without truncation.
        var payloadJson = DraftPayloadJson.Serialize(request.Declaration);
        var inputBytes = System.Text.Encoding.UTF8.GetByteCount(payloadJson);
        if (inputBytes > SellerDraftLimits.DECLARATION_MAX_BYTES)
        {
            return ResultError.Error<DraftSaveResult>(ErrorCodes.ERR_DECLARATION_TOO_LARGE);
        }

        var contentDigest = DraftPayloadJson.ComputeDigest(payloadJson);
        var requestDigest = DraftPayloadJson.ComputeRequestDigest(
            request.AssetId,
            request.AssetVersionId,
            ModerationOperationKinds.DECLARATION_SAVE,
            request.ExpectedWorkspaceRevision,
            payloadJson);

        ModerationDraftSaveResult save = await moderationFoundationStore.SaveDraftRevision(
            new DraftRevisionSaveRequest(
                request.OwnerId,
                request.AssetId,
                request.AssetVersionId,
                ModerationOperationKinds.DECLARATION_SAVE,
                request.OperationId,
                requestDigest,
                payloadJson,
                SellerDraftLimits.DECLARATION_SCHEMA_VERSION,
                contentDigest,
                request.ExpectedWorkspaceRevision,
                // The store writes this inside the mutation transaction; replays never re-write it.
                new AuditEvent(
                    AuditActions.ASSET_DECLARATION_SAVE,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.ASSET,
                    request.AssetId.ToString())),
            cancellationToken);

        if (save.Status == ModerationDraftSaveStatus.SUCCEEDED)
        {
            return Result.Success(new DraftSaveResult(
                save.WorkspaceId ?? Guid.Empty,
                save.WorkspaceRevision,
                (int)save.MetadataRevision!.Value,
                save.Replayed));
        }

        return save.Status switch
        {
            ModerationDraftSaveStatus.STALE_WORKSPACE => Result.Conflict(ErrorCodes.ERR_MODERATION_WORKSPACE_STALE),
            ModerationDraftSaveStatus.OPERATION_CONFLICT => Result.Conflict(ErrorCodes.ERR_MODERATION_OPERATION_CONFLICT),
            ModerationDraftSaveStatus.NOT_FOUND => Result.NotFound(ErrorCodes.ERR_ASSET_DRAFT_NOT_FOUND),
            _ => Result.Forbidden(ErrorCodes.ERR_FORBIDDEN)
        };
    }
}
