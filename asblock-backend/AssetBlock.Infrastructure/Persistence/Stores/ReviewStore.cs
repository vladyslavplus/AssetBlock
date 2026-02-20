using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Reviews;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class ReviewStore(ApplicationDbContext dbContext, ILogger<ReviewStore> logger) : IReviewStore
{
    public async Task<Review> Create(Guid assetId, Guid userId, int rating, string? comment, CancellationToken cancellationToken = default)
    {
        var review = new Review
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            UserId = userId,
            Rating = rating,
            Comment = comment,
            CreatedAt = DateTimeOffset.UtcNow
        };

        dbContext.Reviews.Add(review);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Successfully created review {ReviewId}", review.Id);
            return review;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database operation failed while creating review {ReviewId}", review.Id);
            throw;
        }
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var review = await dbContext.Reviews.FindAsync([id], cancellationToken);
        if (review is not null)
        {
            dbContext.Reviews.Remove(review);
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        return false;
    }

    public Task<bool> Exists(Guid userId, Guid assetId, CancellationToken cancellationToken = default)
    {
        return dbContext.Reviews
            .AsNoTracking()
            .AnyAsync(r => r.UserId == userId && r.AssetId == assetId, cancellationToken);
    }

    public Task<Review?> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Reviews
            .AsNoTracking()
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<PagedResult<Review>> GetPaged(Guid assetId, GetReviewsRequest request, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Reviews
            .AsNoTracking()
            .Include(r => r.User)
            .Where(r => r.AssetId == assetId);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLower();
            query = query.Where(r => r.Comment != null && r.Comment.ToLower().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);

        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) || !GetReviewsRequest.AllowedSortBy.Contains(request.SortBy)
            ? "CreatedAt"
            : request.SortBy.Trim();
            
        var sortKey = sortBy.ToUpperInvariant();
        var isDesc = request.SortDirection == SortDirection.DESC;

        query = sortKey switch
        {
            "RATING" => isDesc ? query.OrderByDescending(r => r.Rating) : query.OrderBy(r => r.Rating),
            _ => isDesc ? query.OrderByDescending(r => r.CreatedAt) : query.OrderBy(r => r.CreatedAt)
        };

        var items = await query
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Review>(items, total, request.Page, request.PageSize);
    }
}
