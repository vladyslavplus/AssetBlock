using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Tags;

namespace AssetBlock.Application.UseCases.Tags.GetTagById;

public sealed record GetTagByIdQuery(Guid Id) : IRequest<Result<TagDto>>;
