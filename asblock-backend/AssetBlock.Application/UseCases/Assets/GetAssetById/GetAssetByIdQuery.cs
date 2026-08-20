using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Assets.GetAssetById;

public sealed record GetAssetByIdQuery(Guid Id) : IRequest<Result<AssetDetailItem>>;
