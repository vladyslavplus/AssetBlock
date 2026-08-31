using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Tags;

namespace AssetBlock.Application.UseCases.Assets.AddAssetTag;

public sealed record AddAssetTagCommand(Guid AssetId, Guid UserId, string TagName) : IRequest<Result<TagDto>>;
