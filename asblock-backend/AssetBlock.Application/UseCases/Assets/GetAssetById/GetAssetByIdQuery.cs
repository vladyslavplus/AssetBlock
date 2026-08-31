using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetAssetById;

public sealed record GetAssetByIdQuery(Guid Id) : IRequest<Result<AssetDetailItem>>;
