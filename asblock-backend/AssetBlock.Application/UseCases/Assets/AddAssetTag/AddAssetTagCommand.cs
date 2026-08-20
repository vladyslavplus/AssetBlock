using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Assets.AddAssetTag;

public sealed record AddAssetTagCommand(Guid AssetId, Guid UserId, string TagName) : IRequest<Result<TagDto>>;
