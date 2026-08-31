using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Tags;

namespace AssetBlock.Application.UseCases.Tags.UpdateTag;

public sealed record UpdateTagCommand(Guid Id, string Name) : IRequest<Result<TagDto>>;
