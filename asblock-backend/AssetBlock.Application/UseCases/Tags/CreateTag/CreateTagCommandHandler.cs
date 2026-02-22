using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Domain.Core.Entities;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Tags.CreateTag;

internal sealed class CreateTagCommandHandler(
    ITagStore tagStore,
    ICacheService cache,
    ILogger<CreateTagCommandHandler> logger) : IRequestHandler<CreateTagCommand, Result<TagDto>>
{
    public async Task<Result<TagDto>> Handle(CreateTagCommand request, CancellationToken cancellationToken)
    {
        var normalizedName = request.Name.Trim().ToLowerInvariant();

        var existing = await tagStore.GetByName(normalizedName, cancellationToken);
        if (existing is null)
        {
            var tag = new Tag
            {
                Id = Guid.NewGuid(),
                Name = normalizedName
            };

            await tagStore.Add(tag, cancellationToken);
            logger.LogInformation("Added new tag: {TagName}", normalizedName);
            await cache.RemoveByPrefix(CacheKeys.TAGS_LIST_PREFIX, cancellationToken);

            return Result.Success(new TagDto(tag.Id, tag.Name));
        }

        return ResultError.Error<TagDto>(ErrorCodes.ERR_TAG_ALREADY_EXISTS);
    }
}
