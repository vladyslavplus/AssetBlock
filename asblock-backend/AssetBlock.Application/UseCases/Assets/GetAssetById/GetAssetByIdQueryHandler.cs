using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using MediatR;

namespace AssetBlock.Application.UseCases.Assets.GetAssetById;

internal sealed class GetAssetByIdQueryHandler(IAssetStore assetStore, IReviewStore reviewStore)
    : IRequestHandler<GetAssetByIdQuery, Result<AssetDetailItem>>
{
    public async Task<Result<AssetDetailItem>> Handle(GetAssetByIdQuery request, CancellationToken cancellationToken)
    {
        var asset = await assetStore.GetById(request.Id, cancellationToken);
        if (asset is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        var tags = asset.AssetTags
            .Select(at => at.Tag.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var averageRating = await reviewStore.GetAverageRatingForAsset(asset.Id, cancellationToken);
        var authorUsername = asset.Author.Username;

        var item = new AssetDetailItem(
            asset.Id,
            asset.Title,
            asset.Description,
            asset.Price,
            asset.CategoryId,
            asset.Category.Name,
            asset.AuthorId,
            authorUsername,
            asset.CreatedAt,
            asset.UpdatedAt,
            tags,
            averageRating);
        return Result.Success(item);
    }
}
