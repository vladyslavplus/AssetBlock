using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IListingCopilotStore
{
    Task<ListingCopilotOwnedVersion?> GetOwnedVersion(
        Guid assetVersionId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListCategoryNames(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListTagNames(CancellationToken cancellationToken = default);

    Task<bool> TryCommitSucceeded(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        ListingCopilotSuggestionWrite suggestion,
        CancellationToken cancellationToken = default);

    Task<ListingCopilotSuggestionDto?> GetSuggestionForOwner(
        Guid assetVersionId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default);
}
