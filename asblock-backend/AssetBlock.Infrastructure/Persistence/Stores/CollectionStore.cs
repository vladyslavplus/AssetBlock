using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Collections;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class CollectionStore(ApplicationDbContext dbContext) : ICollectionStore
{
    private const string UNIQUE_COLLECTION_ASSET = "PK_collection_items";
    private const string UNIQUE_COLLECTION_POSITION = "UIX_collection_items_collection_position";
    private const string LIKE_ESCAPE = "\\";

    public Task<Collection?> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Collections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<Collection?> GetForUpdate(Guid id, CancellationToken cancellationToken = default)
    {
        var lockedId = await dbContext.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM collections WHERE "Id" = {id} FOR UPDATE""")
            .FirstOrDefaultAsync(cancellationToken);

        if (lockedId == Guid.Empty)
        {
            return null;
        }

        return await dbContext.Collections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<CollectionDetailDto?> GetPublicDetail(Guid id, CancellationToken cancellationToken = default)
    {
        var collection = await dbContext.Collections
            .AsNoTracking()
            .Where(c => c.Id == id && c.Status == CollectionStatus.PUBLISHED)
            .Select(c => new
            {
                c.Id,
                c.Title,
                c.Description,
                Status = c.Status.ToString(),
                c.PublishedAt,
                c.ArchivedAt,
                c.CreatedAt,
                c.UpdatedAt,
                c.SellerId,
                SellerUsername = c.Seller.Username
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (collection is null)
        {
            return null;
        }

        List<CollectionItemDto> items = await dbContext.CollectionItems
            .AsNoTracking()
            .Where(i => i.CollectionId == id && i.Asset.DeletedAt == null)
            .OrderBy(i => i.Position)
            .Select(i => new CollectionItemDto(
                i.AssetId,
                i.Asset.Title,
                i.Asset.Price,
                i.Position,
                true,
                null))
            .ToListAsync(cancellationToken);

        if (items.Count == 0)
        {
            return null;
        }

        // Re-number contiguous positions for public projection after soft-deleted omissions.
        var projected = items
            .Select((item, index) => item with { Position = index + 1 })
            .ToList();

        return new CollectionDetailDto(
            collection.Id,
            collection.Title,
            collection.Description,
            collection.Status,
            collection.PublishedAt,
            collection.ArchivedAt,
            collection.CreatedAt,
            collection.UpdatedAt,
            collection.SellerId,
            collection.SellerUsername,
            projected);
    }

    public async Task<CollectionDetailDto?> GetSellerDetail(Guid id, Guid sellerId, CancellationToken cancellationToken = default)
    {
        var collection = await dbContext.Collections
            .AsNoTracking()
            .Where(c => c.Id == id && c.SellerId == sellerId)
            .Select(c => new
            {
                c.Id,
                c.Title,
                c.Description,
                Status = c.Status.ToString(),
                c.PublishedAt,
                c.ArchivedAt,
                c.CreatedAt,
                c.UpdatedAt,
                c.SellerId,
                SellerUsername = c.Seller.Username
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (collection is null)
        {
            return null;
        }

        List<CollectionItemDto> items = await dbContext.CollectionItems
            .AsNoTracking()
            .Where(i => i.CollectionId == id)
            .OrderBy(i => i.Position)
            .Select(i => new CollectionItemDto(
                i.AssetId,
                i.Asset.Title,
                i.Asset.Price,
                i.Position,
                i.Asset.DeletedAt == null,
                i.Asset.DeletedAt == null ? null : "Asset is delisted"))
            .ToListAsync(cancellationToken);

        return new CollectionDetailDto(
            collection.Id,
            collection.Title,
            collection.Description,
            collection.Status,
            collection.PublishedAt,
            collection.ArchivedAt,
            collection.CreatedAt,
            collection.UpdatedAt,
            collection.SellerId,
            collection.SellerUsername,
            items);
    }

    public async Task<PagedResult<CollectionListItemDto>> ListPublic(
        ListCollectionsRequest request,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Collection> query = dbContext.Collections
            .AsNoTracking()
            .Where(c => c.Status == CollectionStatus.PUBLISHED)
            .Where(c => c.Items.Any(i => i.Asset.DeletedAt == null));

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var searchText = request.Search.Trim();
            var likePattern = $"%{EscapeLikePattern(searchText)}%";
            query = query.Where(c =>
                EF.Functions.ILike(c.Title, likePattern, LIKE_ESCAPE)
                || (c.Description != null && EF.Functions.ILike(c.Description, likePattern, LIKE_ESCAPE)));
        }

        var total = await query.CountAsync(cancellationToken);
        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) || !ListCollectionsRequest.AllowedSortBy.Contains(request.SortBy)
            ? "PublishedAt"
            : request.SortBy.Trim();
        var isDesc = request.SortDirection == SortDirection.DESC;

        query = sortBy.ToUpperInvariant() switch
        {
            "CREATEDAT" => isDesc
                ? query.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id)
                : query.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id),
            "TITLE" => isDesc
                ? query.OrderByDescending(c => c.Title).ThenBy(c => c.Id)
                : query.OrderBy(c => c.Title).ThenBy(c => c.Id),
            _ => isDesc
                ? query.OrderByDescending(c => c.PublishedAt).ThenBy(c => c.Id)
                : query.OrderBy(c => c.PublishedAt).ThenBy(c => c.Id)
        };

        var page = Math.Max(PagedRequest.DEFAULT_PAGE, request.Page);
        var pageSize = Math.Clamp(request.PageSize, PagedRequest.MIN_PAGE_SIZE, PagedRequest.MAX_PAGE_SIZE);

        List<CollectionListItemDto> items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CollectionListItemDto(
                c.Id,
                c.Title,
                c.Description,
                c.Status.ToString(),
                c.PublishedAt,
                c.CreatedAt,
                c.SellerId,
                c.Seller.Username,
                c.Items.Count(i => i.Asset.DeletedAt == null),
                c.Items
                    .Where(i => i.Asset.DeletedAt == null)
                    .OrderBy(i => i.Position)
                    .Select(i => (Guid?)i.AssetId)
                    .FirstOrDefault(),
                c.Items
                    .Where(i => i.Asset.DeletedAt == null)
                    .OrderBy(i => i.Position)
                    .Select(i => i.Asset.Title)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);

        return new PagedResult<CollectionListItemDto>(items, total, page, pageSize);
    }

    public async Task<PagedResult<CollectionListItemDto>> ListForSeller(
        Guid sellerId,
        ListMyCollectionsRequest request,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Collection> query = dbContext.Collections.AsNoTracking().Where(c => c.SellerId == sellerId);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var searchText = request.Search.Trim();
            var likePattern = $"%{EscapeLikePattern(searchText)}%";
            query = query.Where(c =>
                EF.Functions.ILike(c.Title, likePattern, LIKE_ESCAPE)
                || (c.Description != null && EF.Functions.ILike(c.Description, likePattern, LIKE_ESCAPE)));
        }

        if (!string.IsNullOrWhiteSpace(request.Status)
            && Enum.TryParse<CollectionStatus>(request.Status.Trim(), ignoreCase: true, out CollectionStatus status))
        {
            query = query.Where(c => c.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);
        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) || !ListMyCollectionsRequest.AllowedSortBy.Contains(request.SortBy)
            ? "UpdatedAt"
            : request.SortBy.Trim();
        var isDesc = request.SortDirection == SortDirection.DESC;

        query = sortBy.ToUpperInvariant() switch
        {
            "CREATEDAT" => isDesc
                ? query.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id)
                : query.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id),
            "TITLE" => isDesc
                ? query.OrderByDescending(c => c.Title).ThenBy(c => c.Id)
                : query.OrderBy(c => c.Title).ThenBy(c => c.Id),
            "STATUS" => isDesc
                ? query.OrderByDescending(c => c.Status).ThenBy(c => c.Id)
                : query.OrderBy(c => c.Status).ThenBy(c => c.Id),
            _ => isDesc
                ? query.OrderByDescending(c => c.UpdatedAt ?? c.CreatedAt).ThenBy(c => c.Id)
                : query.OrderBy(c => c.UpdatedAt ?? c.CreatedAt).ThenBy(c => c.Id)
        };

        var page = Math.Max(PagedRequest.DEFAULT_PAGE, request.Page);
        var pageSize = Math.Clamp(request.PageSize, PagedRequest.MIN_PAGE_SIZE, PagedRequest.MAX_PAGE_SIZE);

        List<CollectionListItemDto> items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CollectionListItemDto(
                c.Id,
                c.Title,
                c.Description,
                c.Status.ToString(),
                c.PublishedAt,
                c.CreatedAt,
                c.SellerId,
                c.Seller.Username,
                c.Items.Count,
                c.Items.OrderBy(i => i.Position).Select(i => (Guid?)i.AssetId).FirstOrDefault(),
                c.Items.OrderBy(i => i.Position).Select(i => i.Asset.Title).FirstOrDefault()))
            .ToListAsync(cancellationToken);

        return new PagedResult<CollectionListItemDto>(items, total, page, pageSize);
    }

    public async Task<Collection> Create(
        Guid sellerId,
        string title,
        string? description,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            SellerId = sellerId,
            Title = title,
            Description = description,
            Status = CollectionStatus.DRAFT,
            CreatedAt = now
        };
        dbContext.Collections.Add(collection);
        await dbContext.SaveChangesAsync(cancellationToken);
        return collection;
    }

    public async Task UpdateMetadata(
        Guid id,
        string title,
        string? description,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await dbContext.Collections
            .Where(c => c.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(c => c.Title, title)
                    .SetProperty(c => c.Description, description)
                    .SetProperty(c => c.UpdatedAt, now),
                cancellationToken);
    }

    public async Task AddItem(Guid collectionId, Guid assetId, CancellationToken cancellationToken = default)
    {
        var maxPosition = await dbContext.CollectionItems
            .Where(i => i.CollectionId == collectionId)
            .MaxAsync(i => (int?)i.Position, cancellationToken) ?? 0;

        var item = new CollectionItem
        {
            CollectionId = collectionId,
            AssetId = assetId,
            Position = maxPosition + 1,
            CreatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            dbContext.CollectionItems.Add(item);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: UNIQUE_COLLECTION_ASSET
            })
        {
            dbContext.Entry(item).State = EntityState.Detached;
            throw new DuplicateCollectionAssetException();
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: UNIQUE_COLLECTION_POSITION
            })
        {
            dbContext.Entry(item).State = EntityState.Detached;
            throw new CollectionItemConcurrencyException();
        }
    }

    public async Task RemoveItem(Guid collectionId, Guid assetId, CancellationToken cancellationToken = default)
    {
        var removed = await dbContext.CollectionItems
            .Where(i => i.CollectionId == collectionId && i.AssetId == assetId)
            .ExecuteDeleteAsync(cancellationToken);

        if (removed == 0)
        {
            return;
        }

        List<CollectionItem> remaining = await dbContext.CollectionItems
            .Where(i => i.CollectionId == collectionId)
            .OrderBy(i => i.Position)
            .ThenBy(i => i.AssetId)
            .ToListAsync(cancellationToken);

        await ApplyContiguousPositions(
            remaining,
            remaining.Select(i => i.AssetId).ToList(),
            cancellationToken);
    }

    public async Task ReorderItems(
        Guid collectionId,
        IReadOnlyList<Guid> orderedAssetIds,
        CancellationToken cancellationToken = default)
    {
        List<CollectionItem> items = await dbContext.CollectionItems
            .Where(i => i.CollectionId == collectionId)
            .ToListAsync(cancellationToken);

        if (items.Count != orderedAssetIds.Count
            || items.Select(i => i.AssetId).ToHashSet().SetEquals(orderedAssetIds) == false)
        {
            throw new InvalidOperationException("Reorder asset id set must match the collection membership exactly.");
        }

        await ApplyContiguousPositions(items, orderedAssetIds, cancellationToken);
    }

    /// <summary>
    /// Two-phase position rewrite: move every row to a free positive offset, then assign contiguous 1..N.
    /// Avoids unique (CollectionId, Position) collisions regardless of EF UPDATE order.
    /// </summary>
    private async Task ApplyContiguousPositions(
        List<CollectionItem> items,
        IReadOnlyList<Guid> orderedAssetIds,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        var tempBase = items.Max(i => i.Position) + items.Count + 1;
        for (var i = 0; i < items.Count; i++)
        {
            items[i].Position = tempBase + i;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        for (var i = 0; i < orderedAssetIds.Count; i++)
        {
            Guid assetId = orderedAssetIds[i];
            items.Single(item => item.AssetId == assetId).Position = i + 1;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> TryPublish(
        Guid id,
        Guid sellerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var activeCount = await CountActiveItems(id, cancellationToken);
        if (activeCount == 0)
        {
            return false;
        }

        var updated = await dbContext.Collections
            .Where(c => c.Id == id && c.SellerId == sellerId && c.Status == CollectionStatus.DRAFT)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(c => c.Status, CollectionStatus.PUBLISHED)
                    .SetProperty(c => c.PublishedAt, now)
                    .SetProperty(c => c.ArchivedAt, (DateTimeOffset?)null)
                    .SetProperty(c => c.UpdatedAt, now),
                cancellationToken);
        return updated == 1;
    }

    public async Task<bool> TryArchive(
        Guid id,
        Guid sellerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var updated = await dbContext.Collections
            .Where(c => c.Id == id && c.SellerId == sellerId && c.Status == CollectionStatus.PUBLISHED)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(c => c.Status, CollectionStatus.ARCHIVED)
                    .SetProperty(c => c.ArchivedAt, now)
                    .SetProperty(c => c.UpdatedAt, now),
                cancellationToken);
        return updated == 1;
    }

    public async Task<bool> TryRestoreToDraft(
        Guid id,
        Guid sellerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var updated = await dbContext.Collections
            .Where(c => c.Id == id && c.SellerId == sellerId && c.Status == CollectionStatus.ARCHIVED)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(c => c.Status, CollectionStatus.DRAFT)
                    .SetProperty(c => c.ArchivedAt, (DateTimeOffset?)null)
                    .SetProperty(c => c.PublishedAt, (DateTimeOffset?)null)
                    .SetProperty(c => c.UpdatedAt, now),
                cancellationToken);
        return updated == 1;
    }

    public Task<int> CountActiveItems(Guid collectionId, CancellationToken cancellationToken = default)
    {
        return dbContext.CollectionItems
            .AsNoTracking()
            .CountAsync(i => i.CollectionId == collectionId && i.Asset.DeletedAt == null, cancellationToken);
    }

    public Task<bool> OwnsActiveAsset(Guid sellerId, Guid assetId, CancellationToken cancellationToken = default)
    {
        return dbContext.Assets
            .AsNoTracking()
            .AnyAsync(a => a.Id == assetId && a.AuthorId == sellerId && a.DeletedAt == null, cancellationToken);
    }

    public Task<Guid?> GetPublishedSellerId(Guid collectionId, CancellationToken cancellationToken = default)
    {
        return dbContext.Collections
            .AsNoTracking()
            .Where(c => c.Id == collectionId
                        && c.Status == CollectionStatus.PUBLISHED
                        && c.Items.Any(i => i.Asset.DeletedAt == null))
            .Select(c => (Guid?)c.SellerId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<Guid?> GetPublishedMemberSellerId(
        Guid collectionId,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.CollectionItems
            .AsNoTracking()
            .Where(i => i.CollectionId == collectionId
                        && i.AssetId == assetId
                        && i.Asset.DeletedAt == null
                        && i.Collection.Status == CollectionStatus.PUBLISHED)
            .Select(i => (Guid?)i.Collection.SellerId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", @"\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
