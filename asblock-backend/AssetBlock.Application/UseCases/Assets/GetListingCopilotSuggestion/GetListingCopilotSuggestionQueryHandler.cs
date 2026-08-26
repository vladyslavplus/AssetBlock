using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Application.UseCases.Assets.GetListingCopilotSuggestion;

internal sealed class GetListingCopilotSuggestionQueryHandler(
    IListingCopilotStore listingCopilotStore)
    : IRequestHandler<GetListingCopilotSuggestionQuery, Result<ListingCopilotSuggestionDto>>
{
    public async Task<Result<ListingCopilotSuggestionDto>> Handle(
        GetListingCopilotSuggestionQuery request,
        CancellationToken cancellationToken)
    {
        var owned = await listingCopilotStore.GetOwnedVersion(request.AssetVersionId, request.OwnerUserId, cancellationToken);
        if (owned is null)
        {
            return Result.NotFound();
        }

        var suggestion = await listingCopilotStore.GetSuggestionForOwner(
            request.AssetVersionId,
            request.OwnerUserId,
            cancellationToken);
        return suggestion is null ? Result.NotFound() : Result.Success(suggestion);
    }
}
