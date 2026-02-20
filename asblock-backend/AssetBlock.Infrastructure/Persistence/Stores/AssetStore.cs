using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class AssetStore(ApplicationDbContext dbContext) : IAssetStore
{
    public async Task<Asset> Add(Asset asset, CancellationToken cancellationToken = default)
    {
        dbContext.Assets.Add(asset);
        await dbContext.SaveChangesAsync(cancellationToken);
        return asset;
    }

    public Task<Asset?> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Assets
            .AsNoTracking()
            .Include(a => a.Category)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    public async Task<PagedResult<Asset>> GetPaged(GetAssetsRequest request, CancellationToken cancellationToken = default)
    {
        IQueryable<Asset> query = dbContext.Assets.AsNoTracking().Include(a => a.Category);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLower();
            query = query.Where(a => a.Title.ToLower().Contains(term) || (a.Description != null && a.Description.ToLower().Contains(term)));
        }
        if (request.CategoryId is { } categoryId)
        {
            query = query.Where(a => a.CategoryId == categoryId);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) || !GetAssetsRequest.AllowedSortBy.Contains(request.SortBy)
            ? "CreatedAt"
            : request.SortBy.Trim();
        var sortKey = sortBy.ToUpperInvariant();
        var isDesc = request.SortDirection == SortDirection.DESC;

        query = sortKey switch
        {
            "TITLE" => isDesc ? query.OrderByDescending(a => a.Title) : query.OrderBy(a => a.Title),
            "PRICE" => isDesc ? query.OrderByDescending(a => a.Price) : query.OrderBy(a => a.Price),
            "ID" => isDesc ? query.OrderByDescending(a => a.Id) : query.OrderBy(a => a.Id),
            _ => isDesc ? query.OrderByDescending(a => a.CreatedAt) : query.OrderBy(a => a.CreatedAt)
        };

        var page = Math.Max(PagedRequest.DEFAULT_PAGE, request.Page);
        var pageSize = Math.Clamp(request.PageSize, PagedRequest.MIN_PAGE_SIZE, PagedRequest.MAX_PAGE_SIZE);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Asset>(items, totalCount, page, pageSize);
    }
}
