using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Users.GetMyListings;

internal sealed class GetMyListingsQueryHandler(IAssetStore assetStore)
    : IRequestHandler<GetMyListingsQuery, Result<Domain.Core.Dto.Paging.PagedResult<SellerAssetListItem>>>
{
    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<SellerAssetListItem>>> Handle(
        GetMyListingsQuery request,
        CancellationToken cancellationToken)
    {
        GetAssetsRequest normalizedRequest = request.Request with { Tags = AssetListNormalization.NormalizeTags(request.Request.Tags) };
        Domain.Core.Dto.Paging.PagedResult<SellerAssetListItem> paged = await assetStore.GetMyListings(request.AuthorId, normalizedRequest, cancellationToken);
        Domain.Core.Dto.Paging.PagedResult<SellerAssetListItem> normalized = AssetListNormalization.NormalizeDescriptions(paged);
        return Result.Success(normalized);
    }
}
