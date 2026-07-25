using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Bundles;
using MediatR;

namespace AssetBlock.Application.UseCases.Bundles.GetBundles;

internal sealed class GetBundlesQueryHandler(IBundleStore bundleStore)
    : IRequestHandler<GetBundlesQuery, Result<Domain.Core.Dto.Paging.PagedResult<BundleListItemDto>>>
{
    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<BundleListItemDto>>> Handle(
        GetBundlesQuery request,
        CancellationToken cancellationToken)
    {
        var paged = await bundleStore.ListPublic(request.Request, cancellationToken);
        return Result.Success(paged);
    }
}
