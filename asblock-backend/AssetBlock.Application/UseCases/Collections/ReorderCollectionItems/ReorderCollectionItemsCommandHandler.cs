using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Collections.ReorderCollectionItems;

internal sealed class ReorderCollectionItemsCommandHandler(
    ICollectionStore collectionStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<ReorderCollectionItemsCommandHandler> logger)
    : IRequestHandler<ReorderCollectionItemsCommand, Result>
{
    public async Task<Result> Handle(ReorderCollectionItemsCommand request, CancellationToken cancellationToken)
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
                        AuditActions.COLLECTION_REORDER,
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

                var detail = await collectionStore.GetSellerDetail(request.CollectionId, request.SellerId, ct);
                if (detail is null)
                {
                    outcome = Result.NotFound(ErrorCodes.ERR_COLLECTION_NOT_FOUND);
                    return;
                }

                var existingIds = detail.Items.Select(i => i.AssetId).ToHashSet();
                if (existingIds.Count != request.AssetIds.Count || !existingIds.SetEquals(request.AssetIds))
                {
                    outcome = Result.Conflict(ErrorCodes.ERR_COLLECTION_STATE_INVALID);
                    return;
                }

                await collectionStore.ReorderItems(request.CollectionId, request.AssetIds, ct);
                await auditWriter.Write(new AuditEvent(
                    AuditActions.COLLECTION_REORDER,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.COLLECTION,
                    request.CollectionId.ToString()), ct);
            }, cancellationToken);
        }
        catch (CollectionItemConcurrencyException)
        {
            return Result.Conflict(ErrorCodes.ERR_COLLECTION_STATE_INVALID);
        }
        catch (InvalidOperationException)
        {
            return Result.Conflict(ErrorCodes.ERR_COLLECTION_STATE_INVALID);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reorder items for collection {CollectionId}", request.CollectionId);
            throw;
        }

        if (outcome is not null)
        {
            return outcome;
        }

        logger.LogInformation("Reordered items for collection {CollectionId}", request.CollectionId);
        return Result.Success();
    }
}
