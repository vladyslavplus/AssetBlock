using AssetBlock.Domain.Dto.Assets;
using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Assets.GetAssets;

public sealed record GetAssetsQuery(GetAssetsRequest Request) : IRequest<Result<AssetBlock.Domain.Dto.Paging.PagedResult<AssetListItem>>>;
