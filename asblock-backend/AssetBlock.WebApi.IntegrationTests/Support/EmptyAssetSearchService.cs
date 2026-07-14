using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;

namespace AssetBlock.WebApi.IntegrationTests.Support;

/// <summary>
/// Integration-test catalog search: IntegrationTesting has no Elasticsearch.
/// Returns an empty page so list endpoints stay available (503 would hide pipeline coverage).
/// </summary>
internal sealed class EmptyAssetSearchService : IAssetSearchService
{
    public Task IndexAsset(AssetDocument document, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DeleteAsset(Guid id, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<PagedResult<AssetDocument>> SearchAssets(
        GetAssetsRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? 10 : request.PageSize;
        return Task.FromResult(new PagedResult<AssetDocument>([], 0, page, pageSize));
    }
}
