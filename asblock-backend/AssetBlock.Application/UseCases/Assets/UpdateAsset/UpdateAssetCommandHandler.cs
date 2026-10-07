using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.UpdateAsset;

/// <summary>
/// Material metadata edits (title/description/category) write draft revisions through the
/// workspace foundation; they never mutate approved public fields directly.
/// Price is rejected here and must use the dedicated price-only operation.
/// </summary>
internal sealed class UpdateAssetCommandHandler(
    IModerationFoundationStore moderationFoundationStore,
    ICategoryStore categoryStore,
    IAuditWriter auditWriter,
    ILogger<UpdateAssetCommandHandler> logger)
    : IRequestHandler<UpdateAssetCommand, Result>
{
    public async Task<Result> Handle(UpdateAssetCommand request, CancellationToken cancellationToken)
    {
        if (request.Price.HasValue)
        {
            return Result.Conflict(ErrorCodes.ERR_PRICE_OPERATION_ONLY);
        }

        // EnsurePreUploadWorkspace doubles as the deletion/ownership guard: a deleted or foreign
        // asset surfaces as InvalidOperationException instead of silently writing draft rows.
        try
        {
            await moderationFoundationStore.EnsurePreUploadWorkspace(
                request.AssetId, request.UserId, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.ASSET_UPDATE,
                AuditOutcome.DENIED,
                AuditResourceTypes.ASSET,
                request.AssetId.ToString()), cancellationToken);
            return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
        }

        if (request.CategoryId.HasValue)
        {
            Category? category = await categoryStore.GetById(request.CategoryId.Value, cancellationToken);
            if (category is null)
            {
                return Result.NotFound(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
            }
        }

        SellerDraftSnapshotDto? snapshot = await moderationFoundationStore.GetOwnerDraftSnapshot(
            request.AssetId, request.UserId, cancellationToken);
        if (snapshot is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        SellerDraftMaterialPayload merged = new(
            Title: request.Title?.Trim() ?? snapshot.Material.Title,
            Description: request.Description ?? snapshot.Material.Description,
            CategoryId: request.CategoryId ?? snapshot.Material.CategoryId,
            Tags: snapshot.Material.Tags);

        var payloadJson = DraftPayloadJson.Serialize(merged);
        var contentDigest = DraftPayloadJson.ComputeDigest(payloadJson);

        // CAS uses the revision of the snapshot the merged payload came from, so a concurrent
        // save between the reads yields 409 instead of silently overwriting newer metadata.
        var requestDigest = DraftPayloadJson.ComputeRequestDigest(
            request.AssetId,
            assetVersionId: null,
            ModerationOperationKinds.DRAFT_SAVE,
            snapshot.WorkspaceRevision,
            payloadJson);

        ModerationDraftSaveResult save = await moderationFoundationStore.SaveDraftRevision(
            new DraftRevisionSaveRequest(
                request.UserId,
                request.AssetId,
                AssetVersionId: null,
                ModerationOperationKinds.DRAFT_SAVE,
                Guid.NewGuid(),
                requestDigest,
                payloadJson,
                SellerDraftLimits.MATERIAL_METADATA_SCHEMA_VERSION,
                contentDigest,
                snapshot.WorkspaceRevision),
            cancellationToken);

        switch (save.Status)
        {
            case ModerationDraftSaveStatus.SUCCEEDED:
                if (!save.Replayed)
                {
                    await auditWriter.WriteBestEffort(new AuditEvent(
                        AuditActions.ASSET_DRAFT_SAVE,
                        AuditOutcome.SUCCESS,
                        AuditResourceTypes.ASSET,
                        request.AssetId.ToString(),
                        new Dictionary<string, object?> { ["revision"] = save.MetadataRevision ?? 0 }), cancellationToken);
                }
                logger.LogInformation("Asset material edit saved as draft revision {Revision} for {AssetId}", save.MetadataRevision, request.AssetId);
                return Result.Success();
            case ModerationDraftSaveStatus.STALE_WORKSPACE:
                return Result.Conflict(ErrorCodes.ERR_MODERATION_WORKSPACE_STALE);
            case ModerationDraftSaveStatus.NOT_FOUND:
                return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
            default:
                return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
        }
    }
}
