using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Collections.ArchiveCollection;

internal sealed class ArchiveCollectionCommandHandler(
    ICollectionStore collectionStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<ArchiveCollectionCommandHandler> logger)
    : IRequestHandler<ArchiveCollectionCommand, Result>
{
    public async Task<Result> Handle(ArchiveCollectionCommand request, CancellationToken cancellationToken)
    {
        Result? outcome = null;
        var archived = false;
        var now = DateTimeOffset.UtcNow;
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                var collection = await collectionStore.GetForUpdate(request.Id, ct);
                if (collection is null)
                {
                    outcome = Result.NotFound(ErrorCodes.ERR_COLLECTION_NOT_FOUND);
                    return;
                }

                if (collection.SellerId != request.SellerId)
                {
                    await auditWriter.Write(new AuditEvent(
                        AuditActions.COLLECTION_ARCHIVE,
                        AuditOutcome.DENIED,
                        AuditResourceTypes.COLLECTION,
                        request.Id.ToString()), ct);
                    outcome = Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
                    return;
                }

                if (collection.Status != CollectionStatus.PUBLISHED)
                {
                    outcome = Result.Conflict(ErrorCodes.ERR_COLLECTION_STATE_INVALID);
                    return;
                }

                archived = await collectionStore.TryArchive(request.Id, request.SellerId, now, ct);
                if (archived)
                {
                    await auditWriter.Write(new AuditEvent(
                        AuditActions.COLLECTION_ARCHIVE,
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
            logger.LogError(ex, "Failed to archive collection {CollectionId}", request.Id);
            throw;
        }

        if (outcome is not null)
        {
            return outcome;
        }

        if (!archived)
        {
            return Result.Conflict(ErrorCodes.ERR_COLLECTION_STATE_INVALID);
        }

        logger.LogInformation("Archived collection {CollectionId}", request.Id);
        return Result.Success();
    }
}