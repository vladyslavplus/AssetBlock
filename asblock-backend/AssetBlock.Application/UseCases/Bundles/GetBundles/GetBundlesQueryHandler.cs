using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Bundles;

namespace AssetBlock.Application.UseCases.Bundles.GetBundles;

internal sealed class GetBundlesQueryHandler(IBundleStore bundleStore)
    : IRequestHandler<GetBundlesQuery, Result<Domain.Core.Dto.Paging.PagedResult<BundleListItemDto>>>
{
    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<BundleListItemDto>>> Handle(
        GetBundlesQuery request,
        CancellationToken cancellationToken)
    {
        Domain.Core.Dto.Paging.PagedResult<BundleListItemDto> paged = await bundleStore.ListPublic(request.Request, cancellationToken);
        return Result.Success(paged);
    }
}
