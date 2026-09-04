using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Collections.PublishCollection;

internal sealed class PublishCollectionCommandHandler(
    ICollectionStore collectionStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<PublishCollectionCommandHandler> logger,
    TimeProvider? timeProvider = null)
    : IRequestHandler<PublishCollectionCommand, Result>
{
    public async Task<Result> Handle(PublishCollectionCommand request, CancellationToken cancellationToken)
    {
        Result? outcome = null;
        var published = false;
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                (Result? failure, _) = await CollectionMutationGuard.ValidateMutable(
                    collectionStore,
                    auditWriter,
                    request.Id,
                    request.SellerId,
                    AuditActions.COLLECTION_PUBLISH,
                    ct,
                    requireDraft: true);

                if (failure is not null)
                {
                    outcome = failure;
                    return;
                }

                var activeCount = await collectionStore.CountActiveItems(request.Id, ct);
                if (activeCount <= 0)
                {
                    outcome = Result.Conflict(ErrorCodes.ERR_COLLECTION_EMPTY);
                    return;
                }

                published = await collectionStore.TryPublish(request.Id, request.SellerId, now, ct);
                if (published)
                {
                    await auditWriter.Write(new AuditEvent(
                        AuditActions.COLLECTION_PUBLISH,
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
            logger.LogError(ex, "Failed to publish collection {CollectionId}", request.Id);
            throw;
        }

        if (outcome is not null)
        {
            return outcome;
        }

        if (!published)
        {
            return Result.Conflict(ErrorCodes.ERR_COLLECTION_STATE_INVALID);
        }

        logger.LogInformation("Published collection {CollectionId}", request.Id);
        return Result.Success();
    }
}
