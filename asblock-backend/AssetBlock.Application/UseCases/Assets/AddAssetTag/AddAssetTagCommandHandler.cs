using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.AddAssetTag;

internal sealed class AddAssetTagCommandHandler(
    IAssetStore assetStore,
    ITagStore tagStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ICacheService cache,
    ILogger<AddAssetTagCommandHandler> logger) : IRequestHandler<AddAssetTagCommand, Result<TagDto>>
{
    public async Task<Result<TagDto>> Handle(AddAssetTagCommand request, CancellationToken cancellationToken)
    {
        var asset = await assetStore.GetOwnership(request.AssetId, cancellationToken);
        if (asset is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        if (asset.AuthorId != request.UserId)
        {
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.ASSET_TAG_ADD,
                AuditOutcome.DENIED,
                AuditResourceTypes.ASSET,
                request.AssetId.ToString()), cancellationToken);
            return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
        }

        if (asset.IsDeleted)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        var normalizedName = request.TagName.Trim().ToLowerInvariant();
        var tag = await tagStore.GetByName(normalizedName, cancellationToken);
        if (tag is null)
        {
            logger.LogDebug("Add tag failed: tag not found {TagName}", normalizedName);
            return Result.NotFound(ErrorCodes.ERR_TAG_NOT_FOUND);
        }

        Result? outcome = null;
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                var added = await assetStore.TryAddTag(asset.Id, tag.Id, ct);
                if (!added)
                {
                    logger.LogDebug("Add tag failed: tag already on asset {AssetId} {TagId}", request.AssetId, tag.Id);
                    outcome = Result.Conflict(ErrorCodes.ERR_ASSET_TAG_ALREADY_EXISTS);
                    return;
                }

                await auditWriter.Write(new AuditEvent(
                    AuditActions.ASSET_TAG_ADD,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.ASSET,
                    asset.Id.ToString(),
                    new Dictionary<string, object?> { ["tagId"] = tag.Id.ToString() }), ct);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add tag to asset: {AssetId}", request.AssetId);
            return Result.Error(ErrorCodes.ERR_INTERNAL);
        }

        if (outcome is not null)
        {
            return outcome;
        }

        try
        {
            await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);
            await cache.RemoveByPrefix(CacheKeys.TAGS_LIST_PREFIX, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cache invalidation failed after add tag {AssetId}", request.AssetId);
        }

        logger.LogInformation("Added tag {TagName} to asset: {AssetId}", normalizedName, asset.Id);
        return Result.Success(new TagDto(tag.Id, tag.Name));
    }
}
