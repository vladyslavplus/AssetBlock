using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Assets;
using MediatR;

namespace AssetBlock.Application.UseCases.Assets.GetAssetById;

public sealed record GetAssetByIdQuery(Guid Id) : IRequest<Result<AssetDetailItem>>;
