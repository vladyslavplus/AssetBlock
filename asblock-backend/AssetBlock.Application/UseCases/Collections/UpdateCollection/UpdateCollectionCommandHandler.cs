using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Collections.UpdateCollection;

internal sealed class UpdateCollectionCommandHandler(
    ICollectionStore collectionStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<UpdateCollectionCommandHandler> logger)
    : IRequestHandler<UpdateCollectionCommand, Result>
{
    public async Task<Result> Handle(UpdateCollectionCommand request, CancellationToken cancellationToken)
    {
        Result? outcome = null;
        var title = request.Title.Trim();
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
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
                        AuditActions.COLLECTION_UPDATE,
                        AuditOutcome.DENIED,
                        AuditResourceTypes.COLLECTION,
                        request.Id.ToString()), ct);
                    outcome = Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
                    return;
                }

                if (collection.Status == CollectionStatus.ARCHIVED)
                {
                    outcome = Result.Conflict(ErrorCodes.ERR_COLLECTION_STATE_INVALID);
                    return;
                }

                await collectionStore.UpdateMetadata(request.Id, title, description, now, ct);
                await auditWriter.Write(new AuditEvent(
                    AuditActions.COLLECTION_UPDATE,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.COLLECTION,
                    request.Id.ToString()), ct);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update collection {CollectionId}", request.Id);
            throw;
        }

        if (outcome is not null)
        {
            return outcome;
        }

        logger.LogInformation("Updated collection {CollectionId}", request.Id);
        return Result.Success();
    }
}
