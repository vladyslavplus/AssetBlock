using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using MediatR;

namespace AssetBlock.Application.UseCases.Assets.GetAssetVersions;

internal sealed class GetAssetVersionsQueryHandler(IAssetStore assetStore)
    : IRequestHandler<GetAssetVersionsQuery, Result<IReadOnlyList<AssetVersionSummaryDto>>>
{
    public async Task<Result<IReadOnlyList<AssetVersionSummaryDto>>> Handle(
        GetAssetVersionsQuery request,
        CancellationToken cancellationToken)
    {
        // Prefer active listing (public). Soft-deleted assets are visible only to author/purchaser.
        var versions = await assetStore.ListVersions(
            request.AssetId,
            includeDeletedAsset: false,
            request.RequesterUserId,
            cancellationToken);

        if (versions.Count == 0 && request.RequesterUserId.HasValue)
        {
            versions = await assetStore.ListVersions(
                request.AssetId,
                includeDeletedAsset: true,
                request.RequesterUserId,
                cancellationToken);
        }

        if (versions.Count == 0)
        {
            var asset = await assetStore.GetById(request.AssetId, cancellationToken);
            if (asset is null)
            {
                return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
            }

            // Soft-deleted listings are not public; unauthorized callers get not found.
            if (asset.DeletedAt is not null)
            {
                return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
            }
        }

        return Result.Success(versions);
    }
}
