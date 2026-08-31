using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
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
        DateTimeOffset now = DateTimeOffset.UtcNow;

        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                (Result? failure, _) = await CollectionMutationGuard.ValidateMutable(
                    collectionStore,
                    auditWriter,
                    request.Id,
                    request.SellerId,
                    AuditActions.COLLECTION_UPDATE,
                    ct);

                if (failure is not null)
                {
                    outcome = failure;
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
