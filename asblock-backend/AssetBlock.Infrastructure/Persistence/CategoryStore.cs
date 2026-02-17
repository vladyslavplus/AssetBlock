using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Categories;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence;

internal sealed class CategoryStore(ApplicationDbContext dbContext) : ICategoryStore
{
    public Task<Category?> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Categories
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<PagedResult<Category>> GetPaged(GetCategoriesRequest request, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Categories.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLower();
            query = query.Where(c => c.Name.ToLower().Contains(term) || c.Slug.ToLower().Contains(term));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) || !GetCategoriesRequest.AllowedSortBy.Contains(request.SortBy)
            ? "Name"
            : request.SortBy;
        var isDesc = request.SortDirection == SortDirection.DESC;

        query = sortBy.ToLowerInvariant() switch
        {
            "slug" => isDesc ? query.OrderByDescending(c => c.Slug) : query.OrderBy(c => c.Slug),
            "id" => isDesc ? query.OrderByDescending(c => c.Id) : query.OrderBy(c => c.Id),
            _ => isDesc ? query.OrderByDescending(c => c.Name) : query.OrderBy(c => c.Name)
        };

        var page = Math.Max(PagedRequest.DEFAULT_PAGE, request.Page);
        var pageSize = Math.Clamp(request.PageSize, PagedRequest.MIN_PAGE_SIZE, PagedRequest.MAX_PAGE_SIZE);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Category>(items, totalCount, page, pageSize);
    }
}
