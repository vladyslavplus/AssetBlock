using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.UpdateAssetPrice;

internal sealed class UpdateAssetPriceCommandHandler(
    IAssetStore assetStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ICacheService cache,
    ILogger<UpdateAssetPriceCommandHandler> logger)
    : IRequestHandler<UpdateAssetPriceCommand, Result>
{
    public async Task<Result> Handle(UpdateAssetPriceCommand request, CancellationToken cancellationToken)
    {
        AssetOwnershipDto? ownership = await assetStore.GetOwnership(request.AssetId, cancellationToken);
        if (ownership is null || ownership.IsDeleted)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        if (ownership.AuthorId != request.OwnerId)
        {
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.ASSET_PRICE_UPDATE,
                AuditOutcome.DENIED,
                AuditResourceTypes.ASSET,
                request.AssetId.ToString()), cancellationToken);
            return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
        }

        var updated = false;
        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            // Price-only: title/description/category stay untouched, so searchable metadata does not change.
            updated = await assetStore.Update(request.AssetId, null, null, request.Price, null, ct);

            if (updated)
            {
                await auditWriter.Write(new AuditEvent(
                    AuditActions.ASSET_PRICE_UPDATE,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.ASSET,
                    request.AssetId.ToString(),
                    new Dictionary<string, object?>
                    {
                        ["price"] = request.Price
                    }), ct);
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
            logger.LogWarning(ex, "Cache invalidation failed after price update {AssetId}", request.AssetId);
        }

        logger.LogInformation("Price updated for asset {AssetId} by owner {OwnerId}", request.AssetId, request.OwnerId);
        return Result.Success();
    }
}
