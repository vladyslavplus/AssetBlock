using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Dto.Assets;
using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Assets.GetAssetById;

internal sealed class GetAssetByIdQueryHandler(IAssetStore assetStore)
    : IRequestHandler<GetAssetByIdQuery, Result<AssetDetailItem>>
{
    public async Task<Result<AssetDetailItem>> Handle(GetAssetByIdQuery request, CancellationToken cancellationToken)
    {
        var asset = await assetStore.GetById(request.Id, cancellationToken);
        if (asset is null)
        {
            return ResultError.Error<AssetDetailItem>(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }
        var item = new AssetDetailItem(
            asset.Id,
            asset.Title,
            asset.Description,
            asset.Price,
            asset.CategoryId,
            asset.Category.Name,
            asset.AuthorId,
            asset.CreatedAt,
            asset.UpdatedAt);
        return Result.Success(item);
    }
}
