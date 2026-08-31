using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetAssets;

public sealed record GetAssetsQuery(GetAssetsRequest Request) : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<AssetListItem>>>;
