using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Assets.DeleteAsset;

public sealed record DeleteAssetCommand(Guid Id, Guid UserId) : IRequest<Result>;
