using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Application.UseCases.Assets.SaveAssetDraft;

internal sealed class SaveAssetDraftCommandHandler(
    IModerationFoundationStore moderationFoundationStore)
    : IRequestHandler<SaveAssetDraftCommand, Result<DraftSaveResult>>
{
    public async Task<Result<DraftSaveResult>> Handle(SaveAssetDraftCommand request, CancellationToken cancellationToken)
    {
        SellerDraftMaterialPayload material = request.Material with
        {
            Title = request.Material.Title.Trim(),
            Description = request.Material.Description?.Trim(),
            Tags = request.Material.Tags.Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length > 0)
                .Distinct()
                .ToList()
        };

        var payloadJson = DraftPayloadJson.Serialize(material);
        var contentDigest = DraftPayloadJson.ComputeDigest(payloadJson);
        var requestDigest = DraftPayloadJson.ComputeRequestDigest(
            request.AssetId,
            assetVersionId: null,
            ModerationOperationKinds.DRAFT_SAVE,
            request.ExpectedWorkspaceRevision,
            payloadJson);

        ModerationDraftSaveResult save = await moderationFoundationStore.SaveDraftRevision(
            new DraftRevisionSaveRequest(
                request.OwnerId,
                request.AssetId,
                AssetVersionId: null,
                ModerationOperationKinds.DRAFT_SAVE,
                request.OperationId,
                requestDigest,
                payloadJson,
                SellerDraftLimits.MATERIAL_METADATA_SCHEMA_VERSION,
                contentDigest,
                request.ExpectedWorkspaceRevision,
                // The store writes this inside the mutation transaction; replays never re-write it.
                new AuditEvent(
                    AuditActions.ASSET_DRAFT_SAVE,
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
