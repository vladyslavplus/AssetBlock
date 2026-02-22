using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Tags;
using MediatR;

namespace AssetBlock.Application.UseCases.Tags.GetTagById;

public sealed record GetTagByIdQuery(Guid Id) : IRequest<Result<TagDto>>;
