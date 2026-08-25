using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Users.GetMyListings;

public sealed record GetMyListingsQuery(Guid AuthorId, GetAssetsRequest Request)
    : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<AssetListItem>>>;
