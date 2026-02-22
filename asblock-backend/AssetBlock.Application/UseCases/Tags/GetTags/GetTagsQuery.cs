using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Tags;
using MediatR;

namespace AssetBlock.Application.UseCases.Tags.GetTags;

public sealed record GetTagsQuery(GetTagsRequest Request) : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<TagDto>>>;
