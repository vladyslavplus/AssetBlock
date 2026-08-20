using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Tags.CreateTag;

public sealed record CreateTagCommand(string Name) : IRequest<Result<TagDto>>;
