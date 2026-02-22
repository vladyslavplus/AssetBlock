using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Search;

internal sealed class ElasticSearchService(
    ElasticsearchClient client,
    ILogger<ElasticSearchService> logger) : IAssetSearchService
{
    private const string INDEX_NAME = "assets";
    private const string FUZZINESS_AUTO = "AUTO";
    private const string FIELD_TAGS_KEYWORD = "tags.keyword";
    private const string FIELD_CATEGORY_ID_KEYWORD = "categoryId.keyword";
    private const string FIELD_PRICE = "price";
    private const string FIELD_CREATED_AT = "createdAt";
    private const string FIELD_TITLE_KEYWORD = "title.keyword";
    private const string FIELD_ID_KEYWORD = "id.keyword";
    private const string FIELD_TITLE = "title";
    private const string FIELD_DESCRIPTION = "description";
    private const string MINIMUM_SHOULD_MATCH_ONE = "1";

    public async Task IndexAsset(AssetDocument document, CancellationToken cancellationToken = default)
    {
        var response = await client.IndexAsync(document, idx => idx.Index(INDEX_NAME), cancellationToken);

        if (!response.IsSuccess())
        {
            logger.LogError("Failed to index asset {AssetId} in Elasticsearch: {DebugInformation}", document.Id, response.DebugInformation);
        }
    }

    public async Task<PagedResult<AssetDocument>> SearchAssets(GetAssetsRequest request, CancellationToken cancellationToken = default)
    {
        var mustQueries = new List<Action<QueryDescriptor<AssetDocument>>>();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var searchText = request.Search.Trim();
            const string titleField = $"{FIELD_TITLE}^2.0";
            const string searchFields = $"{titleField},{FIELD_DESCRIPTION}";

            mustQueries.Add(q => q.Bool(b => b
                .MinimumShouldMatch(MINIMUM_SHOULD_MATCH_ONE)
                .Should(
                    s => s.MultiMatch(m => m
                        .Query(searchText)
                        .Fields(searchFields)
                        .Fuzziness(new Fuzziness(FUZZINESS_AUTO))),
                    s => s.MatchPhrasePrefix(m => m
                        .Field(FIELD_TITLE!)
                        .Query(searchText)),
                    s => s.MatchPhrasePrefix(m => m
                        .Field(FIELD_DESCRIPTION!)
                        .Query(searchText)))));
        }

        if (request.CategoryId.HasValue)
        {
            mustQueries.Add(q => q.Term(t => t.Field(FIELD_CATEGORY_ID_KEYWORD!).Value(request.CategoryId.Value.ToString())));
        }

        if (request.Tags is { Count: > 0 })
        {
            var tagTerms = request.Tags
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length > 0)
                .Distinct()
                .ToList();
            if (tagTerms.Count > 0)
            {
                foreach (var tag in tagTerms)
                {
                    var tagValue = tag;
                    mustQueries.Add(q => q.Term(t => t.Field(FIELD_TAGS_KEYWORD!).Value(tagValue)));
                }
            }
        }

        if (request.MinPrice.HasValue || request.MaxPrice.HasValue)
        {
            mustQueries.Add(q => q.Range(r => r.NumberRange(nr =>
            {
                nr.Field(FIELD_PRICE!);
                if (request.MinPrice.HasValue)
                {
                    nr.Gte((double)request.MinPrice.Value);
                }
                if (request.MaxPrice.HasValue)
                {
                    nr.Lte((double)request.MaxPrice.Value);
                }
            })));
        }

        var sortKey = string.IsNullOrWhiteSpace(request.SortBy) || !GetAssetsRequest.AllowedSortBy.Contains(request.SortBy)
            ? "CreatedAt"
            : request.SortBy.Trim();
        var isDesc = request.SortDirection == SortDirection.DESC;
        var sortOrder = isDesc ? SortOrder.Desc : SortOrder.Asc;

        var sortField = sortKey.ToUpperInvariant() switch
        {
            "TITLE" => FIELD_TITLE_KEYWORD,
            "PRICE" => FIELD_PRICE,
            "ID" => FIELD_ID_KEYWORD,
            _ => FIELD_CREATED_AT
        };

        var searchResponse = await client.SearchAsync<AssetDocument>(s => s
            .Indices(INDEX_NAME)
            .IgnoreUnavailable()
            .From((request.Page - 1) * request.PageSize)
            .Size(request.PageSize)
            .Query(q => q.Bool(b => b.Must(mustQueries.ToArray())))
            .Sort(srt => srt.Field(sortField!, new FieldSort { Order = sortOrder })),
            cancellationToken);

        if (!searchResponse.IsSuccess())
        {
            logger.LogError("Failed to search assets in Elasticsearch: {DebugInformation}", searchResponse.DebugInformation);
            return new PagedResult<AssetDocument>(new List<AssetDocument>(), 0, request.Page, request.PageSize);
        }

        var countResponse = await client.CountAsync<AssetDocument>(c => c
            .Indices(INDEX_NAME)
            .IgnoreUnavailable()
            .Query(q => q.Bool(b => b.Must(mustQueries.ToArray()))),
            cancellationToken);

        return new PagedResult<AssetDocument>(
            searchResponse.Documents.ToList(),
            (int)countResponse.Count,
            request.Page,
            request.PageSize);
    }

    public async Task DeleteAsset(Guid id, CancellationToken cancellationToken = default)
    {
        var response = await client.DeleteAsync<AssetDocument>(id.ToString(), d => d.Index(INDEX_NAME), cancellationToken);

        if (!response.IsSuccess() && response.ElasticsearchServerError?.Status != (int)System.Net.HttpStatusCode.NotFound)
        {
            logger.LogError("Failed to delete asset {AssetId} from Elasticsearch: {DebugInformation}", id, response.DebugInformation);
        }
    }
}
