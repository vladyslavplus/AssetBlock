using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.UpdateAsset;

internal sealed class UpdateAssetCommandHandler(
    IAssetStore assetStore,
    ICategoryStore categoryStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ICacheService cache,
    ILogger<UpdateAssetCommandHandler> logger) : IRequestHandler<UpdateAssetCommand, Result>
{
    public async Task<Result> Handle(UpdateAssetCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var asset = await assetStore.GetById(request.AssetId, cancellationToken);
            if (asset is null)
            {
                return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
            }

            if (asset.AuthorId != request.UserId)
            {
                await auditWriter.WriteBestEffort(new AuditEvent(
                    AuditActions.ASSET_UPDATE,
                    AuditOutcome.DENIED,
                    AuditResourceTypes.ASSET,
                    request.AssetId.ToString()), cancellationToken);
                return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
            }

            if (asset.DeletedAt.HasValue)
            {
                return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
            }

            if (request.CategoryId.HasValue)
            {
                var category = await categoryStore.GetById(request.CategoryId.Value, cancellationToken);
                if (category is null)
                {
                    return Result.NotFound(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
                }
            }

            var changedFields = new List<string>();
            if (request.Title is not null)
            {
                changedFields.Add("title");
            }

            if (request.Description is not null)
            {
                changedFields.Add("description");
            }

            if (request.Price.HasValue)
            {
                changedFields.Add("price");
            }

            if (request.CategoryId.HasValue)
            {
                changedFields.Add("categoryId");
            }

            bool updated = false;
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                updated = await assetStore.Update(
                    request.AssetId,
                    request.Title,
                    request.Description,
                    request.Price,
                    request.CategoryId,
                    ct);

                if (updated)
                {
                    await auditWriter.Write(new AuditEvent(
                        AuditActions.ASSET_UPDATE,
                        AuditOutcome.SUCCESS,
                        AuditResourceTypes.ASSET,
                        request.AssetId.ToString(),
                        new Dictionary<string, object?> { ["changedFields"] = changedFields }), ct);
                }
            }, cancellationToken);

            if (!updated)
            {
                return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
            }

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
                logger.LogWarning(ex, "Cache invalidation failed after asset update {AssetId}", request.AssetId);
            }

            logger.LogInformation("Updated asset: {AssetId}", request.AssetId);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update asset: {AssetId}", request.AssetId);
            return Result.Error(ErrorCodes.ERR_INTERNAL);
        }
    }
}
