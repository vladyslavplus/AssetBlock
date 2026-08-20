using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Bundles;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Bundles.GetMyBundle;

internal sealed class GetMyBundleQueryHandler(IBundleStore bundleStore)
    : IRequestHandler<GetMyBundleQuery, Result<BundleDetailDto>>
{
    public async Task<Result<BundleDetailDto>> Handle(GetMyBundleQuery request, CancellationToken cancellationToken)
    {
        var detail = await bundleStore.GetSellerDetail(request.BundleId, request.SellerId, cancellationToken);
        if (detail is null)
        {
            return Result.NotFound(ErrorCodes.ERR_BUNDLE_NOT_FOUND);
        }

        return Result.Success(detail);
    }
}
