using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class PurchaseStore(ApplicationDbContext dbContext) : IPurchaseStore
{
    public async Task<Purchase> Add(Purchase purchase, CancellationToken cancellationToken = default)
    {
        try
        {
            dbContext.Purchases.Add(purchase);
            await dbContext.SaveChangesAsync(cancellationToken);
            return purchase;
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: PurchaseConfiguration.UNIQUE_USER_ASSET
                    or PurchaseConfiguration.UNIQUE_ORDER_LINE
            })
        {
            dbContext.Entry(purchase).State = EntityState.Detached;
            throw new DuplicateEntitlementException();
        }
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

    public Task<bool> ExistsAny(Guid userId, IReadOnlyList<Guid> assetIds, CancellationToken cancellationToken = default)
    {
        if (assetIds.Count == 0)
        {
            return Task.FromResult(false);
        }

        return dbContext.Purchases
            .AsNoTracking()
            .AnyAsync(p => p.UserId == userId && assetIds.Contains(p.AssetId), cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetOwnedAssetIds(
        Guid userId,
        IReadOnlyList<Guid> assetIds,
        CancellationToken cancellationToken = default)
    {
        if (assetIds.Count == 0)
        {
            return [];
        }

        return await dbContext.Purchases
            .AsNoTracking()
            .Where(p => p.UserId == userId && assetIds.Contains(p.AssetId))
            .Select(p => p.AssetId)
            .ToListAsync(cancellationToken);
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
        var query = dbContext.Purchases.IgnoreQueryFilters().AsNoTracking().Where(p => p.UserId == userId);
        var total = await query.CountAsync(cancellationToken);

        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) || !ListMyPurchasesRequest.AllowedSortBy.Contains(request.SortBy)
            ? "PurchasedAt"
            : request.SortBy.Trim();
        var isDesc = request.SortDirection == SortDirection.DESC;

        query = sortBy.ToUpperInvariant() switch
        {
            "PURCHASEDAT" => isDesc
                ? query.OrderByDescending(p => p.PurchasedAt).ThenBy(p => p.Id)
                : query.OrderBy(p => p.PurchasedAt).ThenBy(p => p.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(request.SortBy), sortBy, $"Unexpected sort key after validation: {sortBy}.")
        };

        var page = Math.Max(PagedRequest.DEFAULT_PAGE, request.Page);
        var pageSize = Math.Clamp(request.PageSize, PagedRequest.MIN_PAGE_SIZE, PagedRequest.MAX_PAGE_SIZE);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PurchaseLibraryItemDto(
                p.Id,
                p.OrderLine.OrderId,
                p.AssetId,
                p.Asset.Title,
                p.Asset.Price,
                p.PurchasedAt,
                p.Asset.Author.Username,
                p.Asset.Reviews.Any(r => r.UserId == userId),
                p.AssetVersion.VersionNumber,
                p.AssetVersionId,
                p.Asset.Versions
                    .Where(v => v.VersionNumber >= p.AssetVersion.VersionNumber)
                    .OrderByDescending(v => v.VersionNumber)
                    .Select(v => v.VersionNumber)
                    .First(),
                p.Asset.Versions
                    .Where(v => v.VersionNumber >= p.AssetVersion.VersionNumber)
                    .OrderByDescending(v => v.VersionNumber)
                    .Select(v => v.Id)
                    .First(),
                p.Asset.Versions
                    .Where(v => v.IsCurrent)
                    .Any(v => v.VersionNumber > p.AssetVersion.VersionNumber),
                p.OrderLine.PricePaid,
                p.OrderLine.Order.Currency,
                p.OrderLine.Order.BundleId != null ? PurchaseSource.BUNDLE : PurchaseSource.ASSET,
                p.OrderLine.Order.BundleId,
                p.OrderLine.Order.BundleId != null
                    ? p.OrderLine.Order.BundleRevision != null
                        ? p.OrderLine.Order.BundleRevision.Title
                        : p.OrderLine.Order.ProductTitle
                    : null))
            .ToListAsync(cancellationToken);

        return new PagedResult<PurchaseLibraryItemDto>(items, total, page, pageSize);
    }
}
