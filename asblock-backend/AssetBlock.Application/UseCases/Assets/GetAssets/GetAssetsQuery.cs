using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Assets;
using MediatR;

namespace AssetBlock.Application.UseCases.Assets.GetAssets;

public sealed record GetAssetsQuery(GetAssetsRequest Request) : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<AssetListItem>>>;
