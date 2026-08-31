using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Application.UseCases.Collections;

/// <summary>
/// Centralized guard for collection mutation preconditions: row lock, ownership verification, and state validation.
/// </summary>
internal static class CollectionMutationGuard
{
    public static async Task<(Result? Failure, Collection? Collection)> ValidateMutable(
        ICollectionStore collectionStore,
        IAuditWriter auditWriter,
        Guid collectionId,
        Guid sellerId,
        string auditAction,
        CancellationToken cancellationToken,
        bool requireDraft = false)
    {
        Collection? collection = await collectionStore.GetForUpdate(collectionId, cancellationToken);
        if (collection is null)
        {
            return (Result.NotFound(ErrorCodes.ERR_COLLECTION_NOT_FOUND), null);
        }

        if (collection.SellerId != sellerId)
        {
            await auditWriter.Write(new AuditEvent(
                auditAction,
                AuditOutcome.DENIED,
                AuditResourceTypes.COLLECTION,
                collectionId.ToString()), cancellationToken);
            return (Result.Forbidden(ErrorCodes.ERR_FORBIDDEN), null);
        }

        if (requireDraft)
        {
            if (collection.Status != CollectionStatus.DRAFT)
            {
                return (Result.Conflict(ErrorCodes.ERR_COLLECTION_STATE_INVALID), null);
            }
        }
        else if (collection.Status == CollectionStatus.ARCHIVED)
        {
            return (Result.Conflict(ErrorCodes.ERR_COLLECTION_STATE_INVALID), null);
        }

        return (null, collection);
    }
}
