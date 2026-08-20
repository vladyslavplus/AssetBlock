using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Bundles;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Bundles.GetBundles;

public sealed record GetBundlesQuery(ListBundlesRequest Request)
    : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<BundleListItemDto>>>;
