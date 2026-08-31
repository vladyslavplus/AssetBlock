using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Reviews;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class ReviewStore(ApplicationDbContext dbContext, ILogger<ReviewStore> logger) : IReviewStore
{
    public async Task<double> GetAverageRatingForAsset(Guid assetId, CancellationToken cancellationToken = default)
    {
        return await dbContext.Assets
            .AsNoTracking()
            .Where(a => a.Id == assetId)
            .Select(a => a.RatingAverage)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Review> Create(Review review, CancellationToken cancellationToken = default)
    {
        var hasAmbientTx = dbContext.Database.CurrentTransaction is not null;
        var tx = hasAmbientTx ? null : await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await LockAssetForUpdate(review.AssetId, cancellationToken);
            dbContext.Reviews.Add(review);
            await dbContext.SaveChangesAsync(cancellationToken);
            await UpdateAssetRatingAggregate(review.AssetId, cancellationToken);
            if (tx is not null)
            {
                await tx.CommitAsync(cancellationToken);
            }
            logger.LogInformation("Successfully created review {ReviewId}", review.Id);
            return review;
        }
        catch (Exception ex)
        {
            if (tx is not null)
            {
                await tx.RollbackAsync(cancellationToken);
            }
            logger.LogError(ex, "Database operation failed while creating review {ReviewId}", review.Id);
            throw;
        }
        finally
        {
            if (tx is not null)
            {
                await tx.DisposeAsync();
            }
        }
    }

    public async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var hasAmbientTx = dbContext.Database.CurrentTransaction is not null;
        var tx = hasAmbientTx ? null : await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            Review? review = await dbContext.Reviews.FindAsync([id], cancellationToken);
            if (review is null)
            {
                if (tx is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                }
                logger.LogWarning("Attempted to delete non-existent review {ReviewId}", id);
                return false;
            }

            Guid assetId = review.AssetId;
            await LockAssetForUpdate(assetId, cancellationToken);
            dbContext.Reviews.Remove(review);
            await dbContext.SaveChangesAsync(cancellationToken);
            await UpdateAssetRatingAggregate(assetId, cancellationToken);
            if (tx is not null)
            {
                await tx.CommitAsync(cancellationToken);
            }
            logger.LogInformation("Successfully deleted review {ReviewId}", id);
            return true;
        }
        catch (Exception ex)
        {
            if (tx is not null)
            {
                await tx.RollbackAsync(cancellationToken);
            }
            logger.LogError(ex, "Database operation failed while deleting review {ReviewId}", id);
            throw;
        }
        finally
        {
            if (tx is not null)
            {
                await tx.DisposeAsync();
            }
        }
    }

    private async Task LockAssetForUpdate(Guid assetId, CancellationToken cancellationToken)
    {
        await dbContext.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM assets WHERE "Id" = {assetId} FOR UPDATE""")
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task UpdateAssetRatingAggregate(Guid assetId, CancellationToken cancellationToken)
    {
        var stats = await dbContext.Reviews
            .AsNoTracking()
            .Where(r => r.AssetId == assetId)
            .GroupBy(r => r.AssetId)
            .Select(g => new
            {
                Count = g.Count(),
                Average = g.Average(r => (double)r.Rating)
            })
            .FirstOrDefaultAsync(cancellationToken);

        var count = stats?.Count ?? 0;
        var average = stats?.Average ?? 0d;

        await dbContext.Assets
            .Where(a => a.Id == assetId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.RatingCount, count)
                .SetProperty(a => a.RatingAverage, average),
                cancellationToken);
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

    public async Task<PagedResult<ReviewListItem>> GetPaged(Guid assetId, GetReviewsRequest request, CancellationToken cancellationToken = default)
    {
        IQueryable<Review> query = dbContext.Reviews
            .AsNoTracking()
            .Where(r => r.AssetId == assetId);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var escapedSearch = request.Search.Trim()
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");
            var term = $"%{escapedSearch}%";
            query = query.Where(r => r.Comment != null && EF.Functions.ILike(r.Comment, term));
        }

        var total = await query.CountAsync(cancellationToken);

        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) || !GetReviewsRequest.AllowedSortBy.Contains(request.SortBy)
            ? "CreatedAt"
            : request.SortBy.Trim();

        var sortKey = sortBy.ToUpperInvariant();
        var isDesc = request.SortDirection == SortDirection.DESC;

        query = sortKey switch
        {
            "RATING" => isDesc ? query.OrderByDescending(r => r.Rating).ThenBy(r => r.Id) : query.OrderBy(r => r.Rating).ThenBy(r => r.Id),
            _ => isDesc ? query.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id) : query.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
        };

        var page = Math.Max(PagedRequest.DEFAULT_PAGE, request.Page);
        var pageSize = Math.Clamp(request.PageSize, PagedRequest.MIN_PAGE_SIZE, PagedRequest.MAX_PAGE_SIZE);
        List<ReviewListItem> items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new ReviewListItem(
                r.Id,
                r.AssetId,
                r.UserId,
                r.User.Username,
                r.Rating,
                r.Comment,
                r.CreatedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<ReviewListItem>(items, total, page, pageSize);
    }
}
