using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Domain.Core.Constants;
using MediatR;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AssetBlock.Application.UseCases.Tags.GetTags;

internal sealed class GetTagsQueryHandler(
    ITagStore tagStore,
    ICacheService cache,
    ILogger<GetTagsQueryHandler> logger) : IRequestHandler<GetTagsQuery, Result<Domain.Core.Dto.Paging.PagedResult<TagDto>>>
{
    private static readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<TagDto>>> Handle(GetTagsQuery request, CancellationToken cancellationToken)
    {
        var cacheKey = CacheKeys.TagsList(request.Request);

        var cached = await cache.GetString(cacheKey, cancellationToken);
        if (cached != null)
        {
            try
            {
                var result = JsonSerializer.Deserialize<Domain.Core.Dto.Paging.PagedResult<TagDto>>(cached, _jsonOptions);
                if (result != null)
                {
                    return Result.Success(result);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Failed to deserialize cached tags from {Key}", cacheKey);
                await cache.RemoveByPrefix(CacheKeys.TAGS_LIST_PREFIX, cancellationToken);
            }
        }

        var tagsPaged = await tagStore.SearchTags(request.Request, cancellationToken);
        var tagDtos = tagsPaged.Items.Select(t => new TagDto(t.Id, t.Name)).ToList();
        var resultPaged = new Domain.Core.Dto.Paging.PagedResult<TagDto>(tagDtos, tagsPaged.TotalCount, tagsPaged.Page, tagsPaged.PageSize);

        await cache.SetString(cacheKey, JsonSerializer.Serialize(resultPaged, _jsonOptions), _cacheExpiration, cancellationToken);
        return Result.Success(resultPaged);
    }
}
