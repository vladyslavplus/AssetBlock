using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Tags.UpdateTag;

public sealed record UpdateTagCommand(Guid Id, string Name) : IRequest<Result<TagDto>>;
