using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Collections.AddCollectionItem;

internal sealed class AddCollectionItemCommandHandler(
    ICollectionStore collectionStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<AddCollectionItemCommandHandler> logger)
    : IRequestHandler<AddCollectionItemCommand, Result>
{
    public async Task<Result> Handle(AddCollectionItemCommand request, CancellationToken cancellationToken)
    {
        Result? outcome = null;
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                var collection = await collectionStore.GetForUpdate(request.CollectionId, ct);
                if (collection is null)
                {
                    outcome = Result.NotFound(ErrorCodes.ERR_COLLECTION_NOT_FOUND);
                    return;
                }

                if (collection.SellerId != request.SellerId)
                {
                    await auditWriter.Write(new AuditEvent(
                        AuditActions.COLLECTION_ITEM_ADD,
                        AuditOutcome.DENIED,
                        AuditResourceTypes.COLLECTION,
                        request.CollectionId.ToString()), ct);
                    outcome = Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
                    return;
                }

                if (collection.Status == CollectionStatus.ARCHIVED)
                {
                    outcome = Result.Conflict(ErrorCodes.ERR_COLLECTION_STATE_INVALID);
                    return;
                }

                var ownsAsset = await collectionStore.OwnsActiveAsset(request.SellerId, request.AssetId, ct);
                if (!ownsAsset)
                {
                    outcome = Result.Conflict(ErrorCodes.ERR_COLLECTION_ASSET_INVALID);
                    return;
                }

                var detail = await collectionStore.GetSellerDetail(request.CollectionId, request.SellerId, ct);
                if (detail is null)
                {
                    outcome = Result.NotFound(ErrorCodes.ERR_COLLECTION_NOT_FOUND);
                    return;
                }

                if (detail.Items.Count >= CollectionConstants.MAX_ITEMS)
                {
                    outcome = Result.Conflict(ErrorCodes.ERR_COLLECTION_STATE_INVALID);
                    return;
                }

                await collectionStore.AddItem(request.CollectionId, request.AssetId, ct);
                await auditWriter.Write(new AuditEvent(
                    AuditActions.COLLECTION_ITEM_ADD,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.COLLECTION,
                    request.CollectionId.ToString(),
                    new Dictionary<string, object?> { ["assetId"] = request.AssetId.ToString() }), ct);
            }, cancellationToken);
        }
        catch (DuplicateCollectionAssetException)
        {
            return Result.Conflict(ErrorCodes.ERR_COLLECTION_DUPLICATE_ASSET);
        }
        catch (CollectionItemConcurrencyException)
        {
            return Result.Conflict(ErrorCodes.ERR_COLLECTION_STATE_INVALID);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to add asset {AssetId} to collection {CollectionId}",
                request.AssetId,
                request.CollectionId);
            throw;
        }

        if (outcome is not null)
        {
            return outcome;
        }

        logger.LogInformation(
            "Added asset {AssetId} to collection {CollectionId}",
            request.AssetId,
            request.CollectionId);
        return Result.Success();
    }
}
