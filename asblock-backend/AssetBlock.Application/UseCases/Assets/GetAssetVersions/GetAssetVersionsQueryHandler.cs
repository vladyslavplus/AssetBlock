using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetAssetVersions;

internal sealed class GetAssetVersionsQueryHandler(IAssetStore assetStore)
    : IRequestHandler<GetAssetVersionsQuery, Result<IReadOnlyList<AssetVersionSummaryDto>>>
{
    public async Task<Result<IReadOnlyList<AssetVersionSummaryDto>>> Handle(
        GetAssetVersionsQuery request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AssetVersionSummaryDto>? versions = await assetStore.ListVersions(
            request.AssetId,
            request.RequesterUserId,
            cancellationToken);

        if (versions is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        return Result.Success(versions);
    }
}
