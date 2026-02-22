using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Assets.DeleteAsset;

public sealed record DeleteAssetCommand(Guid Id, Guid UserId) : IRequest<Result>;
