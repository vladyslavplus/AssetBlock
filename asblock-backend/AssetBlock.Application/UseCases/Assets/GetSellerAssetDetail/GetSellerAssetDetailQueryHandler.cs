using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetSellerAssetDetail;

internal sealed class GetSellerAssetDetailQueryHandler(IAssetStore assetStore)
    : IRequestHandler<GetSellerAssetDetailQuery, Result<SellerAssetDetailItem>>
{
    public async Task<Result<SellerAssetDetailItem>> Handle(
        GetSellerAssetDetailQuery request,
        CancellationToken cancellationToken)
    {
        SellerAssetDetailItem? item = await assetStore.GetOwnedSellerDetail(request.AssetId, request.OwnerUserId, cancellationToken);
        if (item is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        var description = string.IsNullOrWhiteSpace(item.Description) ? null : item.Description;
        return Result.Success(item with { Description = description });
    }
}
