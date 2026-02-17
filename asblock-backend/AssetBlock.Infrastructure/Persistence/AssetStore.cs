using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Dto.Assets;
using AssetBlock.Domain.Dto.Paging;
using AssetBlock.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence;

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
            var term = request.Search.Trim();
            query = query.Where(a => a.Title.Contains(term) || (a.Description != null && a.Description.Contains(term)));
        }
        if (request.CategoryId is { } categoryId)
        {
            query = query.Where(a => a.CategoryId == categoryId);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) || !GetAssetsRequest.AllowedSortBy.Contains(request.SortBy)
            ? "CreatedAt"
            : request.SortBy;
        var isDesc = request.SortDirection == SortDirection.DESC;

        query = sortBy switch
        {
            "Title" => isDesc ? query.OrderByDescending(a => a.Title) : query.OrderBy(a => a.Title),
            "Price" => isDesc ? query.OrderByDescending(a => a.Price) : query.OrderBy(a => a.Price),
            "Id" => isDesc ? query.OrderByDescending(a => a.Id) : query.OrderBy(a => a.Id),
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
