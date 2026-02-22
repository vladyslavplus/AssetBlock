using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Tags;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Tags.UpdateTag;

internal sealed class UpdateTagCommandHandler(
    ITagStore tagStore,
    ICacheService cache,
    ILogger<UpdateTagCommandHandler> logger) : IRequestHandler<UpdateTagCommand, Result<TagDto>>
{
    public async Task<Result<TagDto>> Handle(UpdateTagCommand request, CancellationToken cancellationToken)
    {
        var tag = await tagStore.GetById(request.Id, cancellationToken);
        if (tag is null)
        {
            return ResultError.Error<TagDto>(ErrorCodes.ERR_TAG_NOT_FOUND);
        }

        var normalizedName = request.Name.Trim().ToLowerInvariant();
        if (tag.Name == normalizedName)
        {
            return Result.Success(new TagDto(tag.Id, tag.Name));
        }

        var existing = await tagStore.GetByName(normalizedName, cancellationToken);
        if (existing is not null)
        {
            return ResultError.Error<TagDto>(ErrorCodes.ERR_TAG_ALREADY_EXISTS);
        }

        tag.Name = normalizedName;
        await tagStore.Update(tag, cancellationToken);
        logger.LogInformation("Updated tag {TagId} to name: {TagName}", tag.Id, normalizedName);
        await cache.RemoveByPrefix(CacheKeys.TAGS_LIST_PREFIX, cancellationToken);
        await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);

        return Result.Success(new TagDto(tag.Id, tag.Name));
    }
}
