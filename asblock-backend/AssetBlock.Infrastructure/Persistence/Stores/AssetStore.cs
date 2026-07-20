using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class AssetStore(ApplicationDbContext dbContext) : IAssetStore
{
    private const float TRIGRAM_SIMILARITY_THRESHOLD = 0.30f;
    private const int MIN_TRIGRAM_QUERY_LENGTH = 3;
    private const string LIKE_ESCAPE = "\\";

    public async Task<Asset> Add(Asset asset, CancellationToken cancellationToken = default)
    {
        dbContext.Assets.Add(asset);
        await dbContext.SaveChangesAsync(cancellationToken);
        return asset;
    }

    public async Task<Asset> AddWithTags(Asset asset, List<Tag> tags, CancellationToken cancellationToken = default)
    {
        if (tags.Count > 0)
        {
            foreach (var tag in tags)
            {
                asset.AssetTags.Add(new AssetTag
                {
                    AssetId = asset.Id,
                    TagId = tag.Id
                });
            }
        }

        dbContext.Assets.Add(asset);
        await dbContext.SaveChangesAsync(cancellationToken);
        return asset;
    }

    public async Task<Asset> AddWithVersion(Asset asset, AssetVersion version, List<Tag>? tags, CancellationToken cancellationToken = default)
    {
        if (tags is { Count: > 0 })
        {
            foreach (var tag in tags)
            {
                asset.AssetTags.Add(new AssetTag
                {
                    AssetId = asset.Id,
                    TagId = tag.Id
                });
            }
        }

        dbContext.Assets.Add(asset);
        dbContext.AssetVersions.Add(version);
        await dbContext.SaveChangesAsync(cancellationToken);
        return asset;
    }

    public Task<Asset?> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Assets
            .AsNoTracking()
            .Include(a => a.Category)
            .Include(a => a.Author)
            .Include(a => a.AssetTags).ThenInclude(at => at.Tag)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    public Task<Asset?> GetForUpdate(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Assets
            .FromSqlRaw("SELECT * FROM assets WHERE \"Id\" = {0} FOR UPDATE", id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<AssetCurrentVersionSnapshot?> GetCurrentVersionSnapshot(Guid assetId, CancellationToken cancellationToken = default)
    {
        return dbContext.AssetVersions
            .AsNoTracking()
            .Where(v => v.AssetId == assetId && v.IsCurrent)
            .Select(v => new AssetCurrentVersionSnapshot(
                v.AssetId,
                v.Id,
                v.Asset.AuthorId,
                v.Asset.Title,
                v.Asset.Description,
                v.Asset.Price,
                v.Asset.DeletedAt,
                v.VersionNumber,
                v.FileName,
                v.StorageKey,
                v.ContentLength,
                v.ContentSha256))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<AssetVersion?> GetVersion(Guid assetId, Guid versionId, CancellationToken cancellationToken = default)
    {
        return dbContext.AssetVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.AssetId == assetId && v.Id == versionId, cancellationToken);
    }

    public async Task<IReadOnlyList<AssetVersionSummaryDto>> ListVersions(
        Guid assetId,
        bool includeDeletedAsset,
        Guid? requesterUserId,
        CancellationToken cancellationToken = default)
    {
        var assetQuery = dbContext.Assets.AsNoTracking().Where(a => a.Id == assetId);
        if (!includeDeletedAsset)
        {
            assetQuery = assetQuery.Where(a => a.DeletedAt == null);
        }

        var assetExists = await assetQuery.AnyAsync(cancellationToken);
        if (!assetExists)
        {
            return Array.Empty<AssetVersionSummaryDto>();
        }

        // Active (non-deleted) listings expose version history publicly.
        // Soft-deleted assets require author or entitled purchaser.
        if (includeDeletedAsset)
        {
            var isAuthor = requesterUserId.HasValue
                && await dbContext.Assets.AsNoTracking()
                    .AnyAsync(a => a.Id == assetId && a.AuthorId == requesterUserId.Value, cancellationToken);

            if (!isAuthor)
            {
                if (!requesterUserId.HasValue)
                {
                    return Array.Empty<AssetVersionSummaryDto>();
                }

                var hasPurchase = await dbContext.Purchases.AsNoTracking()
                    .AnyAsync(p => p.AssetId == assetId && p.UserId == requesterUserId.Value, cancellationToken);
                if (!hasPurchase)
                {
                    return Array.Empty<AssetVersionSummaryDto>();
                }
            }
        }

        return await dbContext.AssetVersions
            .AsNoTracking()
            .Where(v => v.AssetId == assetId)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new AssetVersionSummaryDto(
                v.Id,
                v.VersionNumber,
                v.IsCurrent,
                v.FileName,
                v.ContentLength,
                v.ContentSha256,
                v.ReleaseNotes,
                v.CreatedAt,
                new AssetLicenseSummaryDto(
                    v.LicenseCode.ToString(),
                    v.LicenseDisplayName,
                    v.LicenseTemplateVersion,
                    v.LicenseTerms)))
            .ToListAsync(cancellationToken);
    }

    public async Task<AssetVersion> PublishNextVersion(Guid assetId, Guid authorId, AssetVersion draft, CancellationToken cancellationToken = default)
    {
        // Row lock to prevent concurrent publishes on the same asset.
        var asset = await GetForUpdate(assetId, cancellationToken)
            ?? throw new AssetBlock.Domain.Core.Exceptions.AssetNotFoundException();

        if (asset.DeletedAt.HasValue)
        {
            throw new AssetBlock.Domain.Core.Exceptions.AssetNotFoundException();
        }

        if (asset.AuthorId != authorId)
        {
            throw new UnauthorizedAccessException($"User {authorId} is not the author of asset {assetId}.");
        }

        var maxVersion = await dbContext.AssetVersions
            .Where(v => v.AssetId == assetId)
            .MaxAsync(v => (int?)v.VersionNumber, cancellationToken) ?? 0;

        await dbContext.AssetVersions
            .Where(v => v.AssetId == assetId && v.IsCurrent)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, false), cancellationToken);

        draft.AssetId = assetId;
        draft.VersionNumber = maxVersion + 1;
        draft.IsCurrent = true;

        dbContext.AssetVersions.Add(draft);
        await dbContext.SaveChangesAsync(cancellationToken);
        return draft;
    }

    public async Task<IReadOnlyList<string>> GetAllStorageKeys(Guid assetId, CancellationToken cancellationToken = default)
    {
        return await dbContext.AssetVersions
            .AsNoTracking()
            .Where(v => v.AssetId == assetId)
            .Select(v => v.StorageKey)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ExistsByStorageKey(string storageKey, CancellationToken cancellationToken = default)
    {
        return await dbContext.AssetVersions.AsNoTracking()
            .AnyAsync(v => v.StorageKey == storageKey, cancellationToken);
    }

    public async Task<PagedResult<AssetListItem>> GetPaged(GetAssetsRequest request, CancellationToken cancellationToken = default)
    {
        IQueryable<Asset> query = dbContext.Assets.AsNoTracking()
            .Where(a => a.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var searchText = request.Search.Trim();
            var likePattern = $"%{EscapeLikePattern(searchText)}%";

            if (searchText.Length >= MIN_TRIGRAM_QUERY_LENGTH)
            {
                query = query.Where(a =>
                    EF.Property<NpgsqlTsVector>(a, AssetConfiguration.SEARCH_VECTOR_PROPERTY)
                        .Matches(EF.Functions.WebSearchToTsQuery("simple", searchText))
                    || EF.Functions.ILike(a.Title, likePattern, LIKE_ESCAPE)
                    || (a.Description != null && EF.Functions.ILike(a.Description, likePattern, LIKE_ESCAPE))
                    || PostgresDbFunctions.TrigramsSimilarity(a.Title, searchText) >= TRIGRAM_SIMILARITY_THRESHOLD
                    || (a.Description != null
                        && PostgresDbFunctions.TrigramsSimilarity(a.Description, searchText) >= TRIGRAM_SIMILARITY_THRESHOLD));
            }
            else
            {
                query = query.Where(a =>
                    EF.Property<NpgsqlTsVector>(a, AssetConfiguration.SEARCH_VECTOR_PROPERTY)
                        .Matches(EF.Functions.WebSearchToTsQuery("simple", searchText))
                    || EF.Functions.ILike(a.Title, likePattern, LIKE_ESCAPE)
                    || (a.Description != null && EF.Functions.ILike(a.Description, likePattern, LIKE_ESCAPE)));
            }
        }

        if (request.CategoryId is { } categoryId)
        {
            query = query.Where(a => a.CategoryId == categoryId);
        }

        if (request.AuthorId is { } authorId)
        {
            query = query.Where(a => a.AuthorId == authorId);
        }

        if (request.MinPrice is { } minPrice)
        {
            query = query.Where(a => a.Price >= minPrice);
        }

        if (request.MaxPrice is { } maxPrice)
        {
            query = query.Where(a => a.Price <= maxPrice);
        }

        if (request.Tags is { Count: > 0 })
        {
            foreach (var tag in request.Tags)
            {
                var tagName = tag;
                query = query.Where(a => a.AssetTags.Any(at => at.Tag.Name == tagName));
            }
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) || !GetAssetsRequest.AllowedSortBy.Contains(request.SortBy)
            ? "CreatedAt"
            : request.SortBy.Trim();
        var sortKey = sortBy.ToUpperInvariant();
        var isDesc = request.SortDirection == SortDirection.DESC;

        query = sortKey switch
        {
            "TITLE" => isDesc
                ? query.OrderByDescending(a => a.Title).ThenBy(a => a.Id)
                : query.OrderBy(a => a.Title).ThenBy(a => a.Id),
            "PRICE" => isDesc
                ? query.OrderByDescending(a => a.Price).ThenBy(a => a.Id)
                : query.OrderBy(a => a.Price).ThenBy(a => a.Id),
            "ID" => isDesc ? query.OrderByDescending(a => a.Id) : query.OrderBy(a => a.Id),
            _ => isDesc
                ? query.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id)
                : query.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
        };

        var page = Math.Max(PagedRequest.DEFAULT_PAGE, request.Page);
        var pageSize = Math.Clamp(request.PageSize, PagedRequest.MIN_PAGE_SIZE, PagedRequest.MAX_PAGE_SIZE);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AssetListItem(
                a.Id,
                a.Title,
                a.Description,
                a.Price,
                a.CategoryId,
                a.Category.Name,
                a.AuthorId,
                a.Author.Username,
                a.CreatedAt,
                a.AssetTags
                    .Select(at => at.Tag.Name)
                    .OrderBy(n => n)
                    .ToList(),
                a.Reviews.Average(r => (double?)r.Rating) ?? 0d))
            .ToListAsync(cancellationToken);

        return new PagedResult<AssetListItem>(items, totalCount, page, pageSize);
    }

    public async Task SoftDelete(Guid id, DateTimeOffset deletedAt, CancellationToken cancellationToken = default)
    {
        await dbContext.Assets
            .Where(a => a.Id == id && a.DeletedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(a => a.DeletedAt, deletedAt)
                    .SetProperty(a => a.UpdatedAt, deletedAt),
                cancellationToken);
    }

    public async Task Delete(Guid id, CancellationToken cancellationToken = default)
    {
        await dbContext.Assets.Where(a => a.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task AddTag(Guid assetId, Guid tagId, CancellationToken cancellationToken = default)
    {
        const string assetTagPrimaryKey = "PK_asset_tags";
        var assetTag = new AssetTag { AssetId = assetId, TagId = tagId };
        try
        {
            dbContext.Set<AssetTag>().Add(assetTag);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is Npgsql.PostgresException
            {
                SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
                ConstraintName: assetTagPrimaryKey
            })
        {
            // Tag already linked to asset — detach so the scoped context stays usable.
            dbContext.Entry(assetTag).State = EntityState.Detached;
        }
    }

    public Task<bool> HasAssetTag(Guid assetId, Guid tagId, CancellationToken cancellationToken = default)
    {
        return dbContext.Set<AssetTag>().AnyAsync(at => at.AssetId == assetId && at.TagId == tagId, cancellationToken);
    }

    public async Task<bool> RemoveTag(Guid assetId, Guid tagId, CancellationToken cancellationToken = default)
    {
        var deleted = await dbContext.Set<AssetTag>()
            .Where(at => at.AssetId == assetId && at.TagId == tagId)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }

    public async Task<bool> Update(Guid id, string? title, string? description, decimal? price, Guid? categoryId, CancellationToken cancellationToken = default)
    {
        var asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, cancellationToken);
        if (asset is null)
        {
            return false;
        }

        if (title is not null)
        {
            asset.Title = title;
        }
        if (description is not null)
        {
            asset.Description = description;
        }
        if (price.HasValue)
        {
            asset.Price = price.Value;
        }
        if (categoryId.HasValue)
        {
            asset.CategoryId = categoryId.Value;
        }

        asset.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", @"\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
