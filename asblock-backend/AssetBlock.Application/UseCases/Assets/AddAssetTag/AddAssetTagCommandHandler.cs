using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.AddAssetTag;

/// <summary>
/// Tag additions append a draft metadata revision; approved public tags stay untouched.
/// </summary>
internal sealed class AddAssetTagCommandHandler(
    IModerationFoundationStore moderationFoundationStore,
    ITagStore tagStore,
    IAuditWriter auditWriter,
    ILogger<AddAssetTagCommandHandler> logger)
    : IRequestHandler<AddAssetTagCommand, Result<TagDto>>
{
    public async Task<Result<TagDto>> Handle(AddAssetTagCommand request, CancellationToken cancellationToken)
    {
        SellerDraftSnapshotDto? draft = await moderationFoundationStore.GetOwnerDraftSnapshot(
            request.AssetId, request.UserId, cancellationToken);
        if (draft is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        var normalizedName = request.TagName.Trim().ToLowerInvariant();
        Tag? tag = await tagStore.GetByName(normalizedName, cancellationToken);
        if (tag is null)
        {
            logger.LogDebug("Add tag failed: tag not found {TagName}", normalizedName);
            return Result.NotFound(ErrorCodes.ERR_TAG_NOT_FOUND);
        }

        // CAS must compare against the revision of the snapshot the payload was read from;
        // a fresh server revision would silently overwrite a concurrent material save.
        var expectedWorkspaceRevision = draft.WorkspaceRevision;

        SellerDraftMaterialPayload merged = draft.Material with { Tags = draft.Material.Tags.Append(normalizedName).Distinct().ToList() };

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
                AuditActions.ASSET_TAG_ADD,
                AuditOutcome.SUCCESS,
                AuditResourceTypes.ASSET,
                request.AssetId.ToString(),
                new Dictionary<string, object?> { ["tagId"] = tag.Id.ToString() }), cancellationToken);
        }

        logger.LogInformation("Tag {TagName} added to draft workspace of asset {AssetId}", normalizedName, request.AssetId);
        return Result.Success(new TagDto(tag.Id, tag.Name));
    }
}
