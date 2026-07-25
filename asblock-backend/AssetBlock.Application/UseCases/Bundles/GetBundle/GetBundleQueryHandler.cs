using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Bundles;
using MediatR;

namespace AssetBlock.Application.UseCases.Bundles.GetBundle;

internal sealed class GetBundleQueryHandler(IBundleStore bundleStore)
    : IRequestHandler<GetBundleQuery, Result<BundleDetailDto>>
{
    public async Task<Result<BundleDetailDto>> Handle(GetBundleQuery request, CancellationToken cancellationToken)
    {
        var detail = await bundleStore.GetPublicDetail(request.Id, cancellationToken);
        if (detail is null)
        {
            return Result.NotFound(ErrorCodes.ERR_BUNDLE_NOT_FOUND);
        }

        return Result.Success(detail);
    }
}
