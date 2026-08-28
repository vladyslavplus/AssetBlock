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
        var normalizedRequest = request.Request with { Tags = AssetListNormalization.NormalizeTags(request.Request.Tags) };
        var paged = await assetStore.GetMyListings(request.AuthorId, normalizedRequest, cancellationToken);
        var normalized = AssetListNormalization.NormalizeDescriptions(paged);
        return Result.Success(normalized);
    }
}
