using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Collections.RestoreCollection;

internal sealed class RestoreCollectionCommandHandler(
    ICollectionStore collectionStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<RestoreCollectionCommandHandler> logger)
    : IRequestHandler<RestoreCollectionCommand, Result>
{
    public async Task<Result> Handle(RestoreCollectionCommand request, CancellationToken cancellationToken)
    {
        Result? outcome = null;
        var restored = false;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                Collection? collection = await collectionStore.GetForUpdate(request.Id, ct);
                if (collection is null)
                {
                    outcome = Result.NotFound(ErrorCodes.ERR_COLLECTION_NOT_FOUND);
                    return;
                }

                if (collection.SellerId != request.SellerId)
                {
                    await auditWriter.Write(new AuditEvent(
                        AuditActions.COLLECTION_RESTORE,
                        AuditOutcome.DENIED,
                        AuditResourceTypes.COLLECTION,
                        request.Id.ToString()), ct);
                    outcome = Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
                    return;
                }

                if (collection.Status != CollectionStatus.ARCHIVED)
                {
                    outcome = Result.Conflict(ErrorCodes.ERR_COLLECTION_STATE_INVALID);
                    return;
                }

                restored = await collectionStore.TryRestoreToDraft(request.Id, request.SellerId, now, ct);
                if (restored)
                {
                    await auditWriter.Write(new AuditEvent(
                        AuditActions.COLLECTION_RESTORE,
                        AuditOutcome.SUCCESS,
                        AuditResourceTypes.COLLECTION,
                        request.Id.ToString()), ct);
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to restore collection {CollectionId}", request.Id);
            throw;
        }

        if (outcome is not null)
        {
            return outcome;
        }

        if (!restored)
        {
            return Result.Conflict(ErrorCodes.ERR_COLLECTION_STATE_INVALID);
        }

        logger.LogInformation("Restored collection {CollectionId} to draft", request.Id);
        return Result.Success();
    }
}
