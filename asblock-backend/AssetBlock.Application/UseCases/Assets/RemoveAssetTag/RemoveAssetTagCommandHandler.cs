using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.RemoveAssetTag;

internal sealed class RemoveAssetTagCommandHandler(
    IAssetStore assetStore,
    ITagStore tagStore,
    IUnitOfWork unitOfWork,
    IOutboxStore outboxStore,
    ICacheService cache,
    ILogger<RemoveAssetTagCommandHandler> logger) : IRequestHandler<RemoveAssetTagCommand, Result>
{
    public async Task<Result> Handle(RemoveAssetTagCommand request, CancellationToken cancellationToken)
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

        if (asset.DeletedAt.HasValue)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        var tag = await tagStore.GetById(request.TagId, cancellationToken);
        if (tag is null)
        {
            return Result.NotFound(ErrorCodes.ERR_TAG_NOT_FOUND);
        }

        var hasTag = await assetStore.HasAssetTag(asset.Id, tag.Id, cancellationToken);
        if (!hasTag)
        {
            logger.LogDebug("Remove tag failed: tag not on asset {AssetId} {TagId}", request.AssetId, request.TagId);
            return Result.NotFound(ErrorCodes.ERR_ASSET_TAG_NOT_FOUND);
        }

        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                await assetStore.RemoveTag(asset.Id, tag.Id, ct);
                await outboxStore.Enqueue(
                    OutboxMessageTypes.ASSET_INDEX_UPSERT,
                    new AssetIndexUpsertPayload(asset.Id),
                    ct);
            }, cancellationToken);

            try
            {
                await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cache invalidation failed after remove tag {AssetId}", request.AssetId);
            }

            logger.LogInformation("Removed tag {TagId} from asset: {AssetId}", tag.Id, asset.Id);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remove tag from asset: {AssetId}", request.AssetId);
            return Result.Error(ErrorCodes.ERR_INTERNAL);
        }
    }
}
