using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Bundles;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Bundles.GetMyBundles;

public sealed record GetMyBundlesQuery(Guid SellerId, ListMyBundlesRequest Request)
    : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<BundleListItemDto>>>;
