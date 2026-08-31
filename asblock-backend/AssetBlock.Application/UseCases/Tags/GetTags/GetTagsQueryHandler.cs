using Ardalis.Result;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Domain.Core.Entities;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Tags.GetTags;

internal sealed class GetTagsQueryHandler(
    ITagStore tagStore,
    ITypedCache cache,
    ILogger<GetTagsQueryHandler> logger) : IRequestHandler<GetTagsQuery, Result<Domain.Core.Dto.Paging.PagedResult<TagDto>>>
{
    private static readonly TimeSpan _cacheExpiration = CatalogCacheConstants.TAGS_LIST_TTL;

    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<TagDto>>> Handle(GetTagsQuery request, CancellationToken cancellationToken)
    {
        var cacheKey = CacheKeys.TagsList(request.Request);

        Domain.Core.Dto.Paging.PagedResult<TagDto>? cached = await cache.Get<Domain.Core.Dto.Paging.PagedResult<TagDto>>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            logger.LogDebug("Tags list cache hit for key {Key}", cacheKey);
            return Result.Success(cached);
        }

        logger.LogDebug("Tags list cache miss for key {Key}", cacheKey);
        Domain.Core.Dto.Paging.PagedResult<Tag> tagsPaged = await tagStore.SearchTags(request.Request, cancellationToken);
        var tagDtos = tagsPaged.Items.Select(t => new TagDto(t.Id, t.Name)).ToList();
        var resultPaged = new Domain.Core.Dto.Paging.PagedResult<TagDto>(tagDtos, tagsPaged.TotalCount, tagsPaged.Page, tagsPaged.PageSize);

        await cache.Set(cacheKey, resultPaged, _cacheExpiration, cancellationToken);
        return Result.Success(resultPaged);
    }
}
