using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Assets.RemoveAssetTag;

public sealed record RemoveAssetTagCommand(Guid AssetId, Guid UserId, Guid TagId) : IRequest<Result>;
