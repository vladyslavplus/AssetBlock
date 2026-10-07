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

namespace AssetBlock.Application.UseCases.Assets.RemoveAssetTag;

/// <summary>
/// Tag removals append a draft metadata revision; approved public tags stay untouched.
/// </summary>
internal sealed class RemoveAssetTagCommandHandler(
    IModerationFoundationStore moderationFoundationStore,
    ITagStore tagStore,
    IAuditWriter auditWriter,
    ILogger<RemoveAssetTagCommandHandler> logger)
    : IRequestHandler<RemoveAssetTagCommand, Result>
{
    public async Task<Result> Handle(RemoveAssetTagCommand request, CancellationToken cancellationToken)
    {
        Tag? tag = await tagStore.GetById(request.TagId, cancellationToken);
        if (tag is null)
        {
            return Result.NotFound(ErrorCodes.ERR_TAG_NOT_FOUND);
        }

        SellerDraftSnapshotDto? draft = await moderationFoundationStore.GetOwnerDraftSnapshot(
            request.AssetId, request.UserId, cancellationToken);
        if (draft is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        var normalizedName = tag.Name.Trim().ToLowerInvariant();
        var remaining = draft.Material.Tags
            .Where(t => !string.Equals(t, normalizedName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (remaining.Count == draft.Material.Tags.Count)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_TAG_NOT_FOUND);
        }

        // CAS must compare against the revision of the snapshot the payload was read from;
        // a fresh server revision would silently overwrite a concurrent material save.
        var expectedWorkspaceRevision = draft.WorkspaceRevision;

        SellerDraftMaterialPayload merged = draft.Material with { Tags = remaining };

        var payloadJson = DraftPayloadJson.Serialize(merged);
        var contentDigest = DraftPayloadJson.ComputeDigest(payloadJson);
        var requestDigest = DraftPayloadJson.ComputeRequestDigest(
            request.AssetId,
            assetVersionId: null,
            ModerationOperationKinds.DRAFT_SAVE,
            expectedWorkspaceRevision,
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
                expectedWorkspaceRevision),
            cancellationToken);

        if (save.Status != ModerationDraftSaveStatus.SUCCEEDED)
        {
            return Result.Conflict(ErrorCodes.ERR_MODERATION_WORKSPACE_STALE);
        }

        if (!save.Replayed)
        {
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.ASSET_TAG_REMOVE,
                AuditOutcome.SUCCESS,
                AuditResourceTypes.ASSET,
                request.AssetId.ToString()), cancellationToken);
        }

        logger.LogInformation("Tag removed from draft workspace of asset {AssetId}", request.AssetId);
        return Result.Success();
    }
}
