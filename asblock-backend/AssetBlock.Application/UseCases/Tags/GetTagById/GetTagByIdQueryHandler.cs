using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Tags;
using MediatR;

namespace AssetBlock.Application.UseCases.Tags.GetTagById;

internal sealed class GetTagByIdQueryHandler(ITagStore tagStore) : IRequestHandler<GetTagByIdQuery, Result<TagDto>>
{
    public async Task<Result<TagDto>> Handle(GetTagByIdQuery request, CancellationToken cancellationToken)
    {
        var tag = await tagStore.GetById(request.Id, cancellationToken);
        return tag is null ? Result.NotFound(ErrorCodes.ERR_TAG_NOT_FOUND) : Result.Success(new TagDto(tag.Id, tag.Name));
    }
}
