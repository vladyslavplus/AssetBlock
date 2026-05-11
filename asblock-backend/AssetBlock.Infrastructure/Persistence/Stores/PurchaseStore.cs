using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class PurchaseStore(ApplicationDbContext dbContext) : IPurchaseStore
{
    public async Task<Purchase> Add(Purchase purchase, CancellationToken cancellationToken = default)
    {
        dbContext.Purchases.Add(purchase);
        await dbContext.SaveChangesAsync(cancellationToken);
        return purchase;
    }

    public Task<bool> HasPurchasesForAsset(Guid assetId, CancellationToken cancellationToken = default)
    {
        return dbContext.Purchases.AsNoTracking().AnyAsync(p => p.AssetId == assetId, cancellationToken);
    }

    public Task<bool> Exists(Guid userId, Guid assetId, CancellationToken cancellationToken = default)
    {
        return dbContext.Purchases
            .AsNoTracking()
            .AnyAsync(p => p.UserId == userId && p.AssetId == assetId, cancellationToken);
    }

    public Task<Purchase?> GetByStripePaymentId(string stripePaymentId, CancellationToken cancellationToken = default)
    {
        return dbContext.Purchases
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.StripePaymentId == stripePaymentId, cancellationToken);
    }

    public Task<Purchase?> GetPurchase(Guid userId, Guid assetId, CancellationToken cancellationToken = default)
    {
        return dbContext.Purchases
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.AssetId == assetId, cancellationToken);
    }

    public async Task<PagedResult<PurchaseLibraryItemDto>> ListForUser(
        Guid userId,
        ListMyPurchasesRequest request,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Purchases.AsNoTracking().Where(p => p.UserId == userId);
        var total = await query.CountAsync(cancellationToken);

        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) || !ListMyPurchasesRequest.AllowedSortBy.Contains(request.SortBy)
            ? "PurchasedAt"
            : request.SortBy.Trim();
        var sortKey = sortBy.ToUpperInvariant();
        var isDesc = request.SortDirection == SortDirection.DESC;

        query = sortKey switch
        {
            "PURCHASEDAT" => isDesc ? query.OrderByDescending(p => p.PurchasedAt) : query.OrderBy(p => p.PurchasedAt),
            _ => throw new ArgumentOutOfRangeException(nameof(request.SortBy), sortBy, $"Unexpected sort key after validation: {sortBy}.")
        };

        var page = Math.Max(PagedRequest.DEFAULT_PAGE, request.Page);
        var pageSize = Math.Clamp(request.PageSize, PagedRequest.MIN_PAGE_SIZE, PagedRequest.MAX_PAGE_SIZE);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PurchaseLibraryItemDto(
                p.Id,
                p.AssetId,
                p.Asset.Title,
                p.Asset.Price,
                p.PurchasedAt,
                p.Asset.Author.Username,
                p.Asset.Reviews.Any(r => r.UserId == userId)))
            .ToListAsync(cancellationToken);

        return new PagedResult<PurchaseLibraryItemDto>(items, total, page, pageSize);
    }
}
