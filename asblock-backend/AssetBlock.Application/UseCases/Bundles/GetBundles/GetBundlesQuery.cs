using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Bundles;

namespace AssetBlock.Application.UseCases.Bundles.GetBundles;

public sealed record GetBundlesQuery(ListBundlesRequest Request)
    : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<BundleListItemDto>>>;
