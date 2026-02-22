using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class TagStore(ApplicationDbContext dbContext) : ITagStore
{
    public async Task<PagedResult<Tag>> SearchTags(GetTagsRequest request, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Tags.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLowerInvariant();
            query = query.Where(t => t.Name.Contains(term));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) || !GetTagsRequest.AllowedSortBy.Contains(request.SortBy.ToLowerInvariant())
            ? "name"
            : request.SortBy.ToLowerInvariant();
            
        var isDesc = request.SortDirection == SortDirection.DESC;

        query = sortBy switch
        {
            "id" => isDesc ? query.OrderByDescending(t => t.Id) : query.OrderBy(t => t.Id),
            _ => isDesc ? query.OrderByDescending(t => t.Name) : query.OrderBy(t => t.Name)
        };

        var page = Math.Max(PagedRequest.DEFAULT_PAGE, request.Page);
        var pageSize = Math.Clamp(request.PageSize, PagedRequest.MIN_PAGE_SIZE, PagedRequest.MAX_PAGE_SIZE);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Tag>(items, totalCount, page, pageSize);
    }

    public Task<List<Tag>> GetTagsByNames(List<string> names, CancellationToken cancellationToken = default)
    {
        var lowerNames = names.Select(n => n.Trim().ToLowerInvariant()).ToList();
        return dbContext.Tags.AsNoTracking().Where(t => lowerNames.Contains(t.Name)).ToListAsync(cancellationToken);
    }

    public Task<Tag?> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Tags.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    public Task<Tag?> GetByName(string name, CancellationToken cancellationToken = default)
    {
        var lowerName = name.Trim().ToLowerInvariant();
        return dbContext.Tags.AsNoTracking().FirstOrDefaultAsync(t => t.Name == lowerName, cancellationToken);
    }

    public async Task<Tag> Add(Tag tag, CancellationToken cancellationToken = default)
    {
        dbContext.Tags.Add(tag);
        await dbContext.SaveChangesAsync(cancellationToken);
        return tag;
    }

    public async Task<Tag> Update(Tag tag, CancellationToken cancellationToken = default)
    {
        dbContext.Tags.Update(tag);
        await dbContext.SaveChangesAsync(cancellationToken);
        return tag;
    }

    public async Task Delete(Tag tag, CancellationToken cancellationToken = default)
    {
        dbContext.Tags.Remove(tag);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
