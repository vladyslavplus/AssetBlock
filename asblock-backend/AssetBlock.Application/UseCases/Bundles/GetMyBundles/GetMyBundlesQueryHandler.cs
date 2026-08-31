using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Bundles;

namespace AssetBlock.Application.UseCases.Bundles.GetMyBundles;

internal sealed class GetMyBundlesQueryHandler(IBundleStore bundleStore)
    : IRequestHandler<GetMyBundlesQuery, Result<Domain.Core.Dto.Paging.PagedResult<BundleListItemDto>>>
{
    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<BundleListItemDto>>> Handle(
        GetMyBundlesQuery request,
        CancellationToken cancellationToken)
    {
        Domain.Core.Dto.Paging.PagedResult<BundleListItemDto> paged = await bundleStore.ListForSeller(request.SellerId, request.Request, cancellationToken);
        return Result.Success(paged);
    }
}
