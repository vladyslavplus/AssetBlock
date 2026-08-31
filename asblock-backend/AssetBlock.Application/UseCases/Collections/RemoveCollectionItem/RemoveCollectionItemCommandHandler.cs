using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Collections;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Collections.RemoveCollectionItem;

internal sealed class RemoveCollectionItemCommandHandler(
    ICollectionStore collectionStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<RemoveCollectionItemCommandHandler> logger)
    : IRequestHandler<RemoveCollectionItemCommand, Result>
{
    public async Task<Result> Handle(RemoveCollectionItemCommand request, CancellationToken cancellationToken)
    {
        Result? outcome = null;
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                (Result? failure, _) = await CollectionMutationGuard.ValidateMutable(
                    collectionStore,
                    auditWriter,
                    request.CollectionId,
                    request.SellerId,
                    AuditActions.COLLECTION_ITEM_REMOVE,
                    ct);

                if (failure is not null)
                {
                    outcome = failure;
                    return;
                }

                CollectionDetailDto? detail = await collectionStore.GetSellerDetail(request.CollectionId, request.SellerId, ct);
                if (detail is null)
                {
                    outcome = Result.NotFound(ErrorCodes.ERR_COLLECTION_NOT_FOUND);
                    return;
                }

                if (detail.Items.All(i => i.AssetId != request.AssetId))
                {
                    outcome = Result.Conflict(ErrorCodes.ERR_COLLECTION_ASSET_INVALID);
                    return;
                }

                await collectionStore.RemoveItem(request.CollectionId, request.AssetId, ct);
                await auditWriter.Write(new AuditEvent(
                    AuditActions.COLLECTION_ITEM_REMOVE,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.COLLECTION,
                    request.CollectionId.ToString(),
                    new Dictionary<string, object?> { ["assetId"] = request.AssetId.ToString() }), ct);
            }, cancellationToken);
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
                "Failed to remove asset {AssetId} from collection {CollectionId}",
                request.AssetId,
                request.CollectionId);
            throw;
        }

        if (outcome is not null)
        {
            return outcome;
        }

        logger.LogInformation(
            "Removed asset {AssetId} from collection {CollectionId}",
            request.AssetId,
            request.CollectionId);
        return Result.Success();
    }
}
