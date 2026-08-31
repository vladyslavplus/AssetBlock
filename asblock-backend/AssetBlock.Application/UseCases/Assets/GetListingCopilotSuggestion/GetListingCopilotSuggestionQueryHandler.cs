using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
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
        ListingCopilotOwnedVersion? owned = await listingCopilotStore.GetOwnedVersion(request.AssetVersionId, request.OwnerUserId, cancellationToken);
        if (owned is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        ListingCopilotSuggestionDto? suggestion = await listingCopilotStore.GetSuggestionForOwner(
            request.AssetVersionId,
            request.OwnerUserId,
            cancellationToken);
        return suggestion is null ? Result.NotFound(ErrorCodes.ERR_NOT_FOUND) : Result.Success(suggestion);
    }
}
