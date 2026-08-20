using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Collections;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Collections.CreateCollection;

internal sealed class CreateCollectionCommandHandler(
    ICollectionStore collectionStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<CreateCollectionCommandHandler> logger)
    : IRequestHandler<CreateCollectionCommand, Result<CreateCollectionResponse>>
{
    public async Task<Result<CreateCollectionResponse>> Handle(
        CreateCollectionCommand request,
        CancellationToken cancellationToken)
    {
        var title = request.Title.Trim();
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();

        Guid collectionId = Guid.Empty;
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                var collection = await collectionStore.Create(request.SellerId, title, description, ct);
                collectionId = collection.Id;
                await auditWriter.Write(new AuditEvent(
                    AuditActions.COLLECTION_CREATE,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.COLLECTION,
                    collection.Id.ToString()), ct);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create collection for seller {SellerId}", request.SellerId);
            throw;
        }

        logger.LogInformation("Created collection {CollectionId} for seller {SellerId}", collectionId, request.SellerId);
        return Result.Success(new CreateCollectionResponse(collectionId));
    }
}
