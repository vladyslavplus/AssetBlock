using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Tags;

namespace AssetBlock.Application.UseCases.Tags.CreateTag;

public sealed record CreateTagCommand(string Name) : IRequest<Result<TagDto>>;
