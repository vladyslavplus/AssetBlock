using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Users.GetMyListings;

internal sealed class GetMyListingsQueryHandler(IAssetStore assetStore)
    : IRequestHandler<GetMyListingsQuery, Result<Domain.Core.Dto.Paging.PagedResult<SellerAssetListItem>>>
{
    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<SellerAssetListItem>>> Handle(
        GetMyListingsQuery request,
        CancellationToken cancellationToken)
    {
        var normalizedRequest = request.Request with { Tags = NormalizeTags(request.Request.Tags) };
        var paged = await assetStore.GetMyListings(request.AuthorId, normalizedRequest, cancellationToken);
        var normalized = NormalizeDescriptions(paged);
        return Result.Success(normalized);
    }

    private static Domain.Core.Dto.Paging.PagedResult<SellerAssetListItem> NormalizeDescriptions(
        Domain.Core.Dto.Paging.PagedResult<SellerAssetListItem> paged)
    {
        var items = paged.Items
            .Select(i => i with { Description = string.IsNullOrWhiteSpace(i.Description) ? null : i.Description })
            .ToList();
        return new Domain.Core.Dto.Paging.PagedResult<SellerAssetListItem>(items, paged.TotalCount, paged.Page, paged.PageSize);
    }

    private static List<string>? NormalizeTags(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return null;
        }
        var list = tags
            .SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();
        return list.Count > 0 ? list : null;
    }
}
