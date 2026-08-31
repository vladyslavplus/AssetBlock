using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Application.UseCases.Assets.DeleteAsset;

internal sealed class DeleteAssetCommandHandler(
    IAssetStore assetStore,
    IPurchaseStore purchaseStore,
    ICheckoutIntentStore checkoutIntentStore,
    IUnitOfWork unitOfWork,
    IOutboxStore outboxStore,
    IAuditWriter auditWriter,
    ICacheService cache,
    ILogger<DeleteAssetCommandHandler> logger) : IRequestHandler<DeleteAssetCommand, Result>
{
    public async Task<Result> Handle(DeleteAssetCommand request, CancellationToken cancellationToken)
    {
        Asset? asset = await assetStore.GetById(request.Id, cancellationToken);
        if (asset is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        if (asset.AuthorId != request.UserId)
        {
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.ASSET_DELETE,
                AuditOutcome.DENIED,
                AuditResourceTypes.ASSET,
                request.Id.ToString()), cancellationToken);
            return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
        }

        try
        {
            var alreadyDeleted = false;
            var softDeleted = false;
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                Asset lockedAsset = await assetStore.GetForUpdate(request.Id, ct)
                    ?? throw new AssetNotFoundException();
                if (lockedAsset.AuthorId != request.UserId)
                {
                    throw new UnauthorizedAccessException();
                }

                if (lockedAsset.DeletedAt.HasValue)
                {
                    alreadyDeleted = true;
                    return;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                var hasPurchases = await purchaseStore.HasPurchasesForAsset(request.Id, ct);
                var hasActiveCheckout = await checkoutIntentStore.HasActiveForAsset(request.Id, now, ct);
                softDeleted = hasPurchases || hasActiveCheckout;

                if (softDeleted)
                {
                    await assetStore.SoftDelete(lockedAsset.Id, now, ct);
                    await auditWriter.Write(new AuditEvent(
                        AuditActions.ASSET_SOFT_DELETE,
                        AuditOutcome.SUCCESS,
                        AuditResourceTypes.ASSET,
                        lockedAsset.Id.ToString(),
                        new Dictionary<string, object?> { ["activeCheckoutPending"] = hasActiveCheckout }), ct);
                    return;
                }

                await checkoutIntentStore.DeleteTerminalUnpaidReferencingAsset(request.Id, ct);

                IReadOnlyList<string> allStorageKeys = await assetStore.GetAllStorageKeys(request.Id, ct);
                await assetStore.Delete(lockedAsset.Id, ct);
                foreach (var key in allStorageKeys)
                {
                    await outboxStore.Enqueue(
                        OutboxMessageTypes.ASSET_BLOB_DELETE,
                        new AssetBlobDeletePayload(lockedAsset.Id, key),
                        ct);
                }

                await auditWriter.Write(new AuditEvent(
                    AuditActions.ASSET_HARD_DELETE,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.ASSET,
                    lockedAsset.Id.ToString()), ct);
            }, cancellationToken);

            if (alreadyDeleted)
            {
                logger.LogInformation("Delete idempotent: asset already delisted {AssetId}", request.Id);
                return Result.Success();
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
                logger.LogWarning(ex, "Cache invalidation failed after delete {AssetId}", request.Id);
            }

            if (softDeleted)
            {
                logger.LogInformation(
                    "Soft-deleted (delisted) asset {AssetId}: purchase or checkout exists; blobs retained.",
                    request.Id);
            }
            else
            {
                logger.LogInformation(
                    "Hard-deleted asset {AssetId}; blob delete enqueued.",
                    request.Id);
            }
            return Result.Success();
        }
        catch (AssetNotFoundException)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }
        catch (UnauthorizedAccessException)
        {
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.ASSET_DELETE,
                AuditOutcome.DENIED,
                AuditResourceTypes.ASSET,
                request.Id.ToString()), cancellationToken);
            return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error deleting asset {AssetId}", request.Id);
            throw;
        }
    }
}
