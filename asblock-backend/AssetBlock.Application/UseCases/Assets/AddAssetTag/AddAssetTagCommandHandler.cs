using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.Events;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Tags;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.AddAssetTag;

internal sealed class AddAssetTagCommandHandler(
    IAssetStore assetStore,
    ITagStore tagStore,
    IPublisher publisher,
    ICacheService cache,
    ILogger<AddAssetTagCommandHandler> logger) : IRequestHandler<AddAssetTagCommand, Result<TagDto>>
{
    public async Task<Result<TagDto>> Handle(AddAssetTagCommand request, CancellationToken cancellationToken)
    {
        var asset = await assetStore.GetById(request.AssetId, cancellationToken);
        if (asset is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        if (asset.AuthorId != request.UserId)
        {
            return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
        }

        var normalizedName = request.TagName.Trim().ToLowerInvariant();
        var tag = await tagStore.GetByName(normalizedName, cancellationToken);
        if (tag is null)
        {
            logger.LogDebug("Add tag failed: tag not found {TagName}", normalizedName);
            return Result.NotFound(ErrorCodes.ERR_TAG_NOT_FOUND);
        }

        if (asset.AssetTags.Any(at => at.TagId == tag.Id))
        {
            logger.LogDebug("Add tag failed: tag already on asset {AssetId} {TagId}", request.AssetId, tag.Id);
            return Result.Conflict(ErrorCodes.ERR_ASSET_TAG_ALREADY_EXISTS);
        }

        try
        {
            await assetStore.AddTag(asset.Id, tag.Id, cancellationToken);

            await publisher.Publish(new AssetCreatedEvent(asset.Id), cancellationToken);

            await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);
            await cache.RemoveByPrefix(CacheKeys.TAGS_LIST_PREFIX, cancellationToken);

            logger.LogInformation("Added tag {TagName} to asset: {AssetId}", normalizedName, asset.Id);
            return Result.Success(new TagDto(tag.Id, tag.Name));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add tag to asset: {AssetId}", request.AssetId);
            return Result.Error(ErrorCodes.ERR_BAD_REQUEST);
        }
    }
}
