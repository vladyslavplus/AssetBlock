using AssetBlock.Domain.Dto.Assets;
using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Assets.GetAssetById;

public sealed record GetAssetByIdQuery(Guid Id) : IRequest<Result<AssetDetailItem>>;
