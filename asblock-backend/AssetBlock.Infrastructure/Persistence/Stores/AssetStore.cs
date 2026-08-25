using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
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

        var dbNow = await dbContext.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT clock_timestamp() AS "Value" """).FirstAsync(cancellationToken);

        if (asset.CreatedAt == default)
        {
            asset.CreatedAt = dbNow;
        }

        if (version.CreatedAt == default)
        {
            version.CreatedAt = dbNow;
        }

        version.ProcessingUpdatedAt = dbNow;

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

    public async Task<Asset?> GetForUpdate(Guid id, CancellationToken cancellationToken = default)
    {
        // FOR UPDATE locks the row for the ambient transaction; AsNoTracking returns a fresh
        // projection without detaching tracked entities (which would drop pending UoW changes).
        // SoftDelete syncs DeletedAt on any local tracker instance after ExecuteUpdate.
        return await dbContext.Assets
            .FromSqlRaw("""SELECT * FROM assets WHERE "Id" = {0} FOR UPDATE""", id)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<AssetCurrentVersionSnapshot?> GetCurrentVersionSnapshot(Guid assetId, CancellationToken cancellationToken = default)
    {
        return dbContext.AssetVersions
            .AsNoTracking()
            .Where(v => v.AssetId == assetId && v.IsCurrent && v.ProcessingStatus == AssetVersionProcessingStatus.READY)
            .Select(v => new AssetCurrentVersionSnapshot(
                v.AssetId,
                v.Id,
                v.Asset.AuthorId,
                v.Asset.Title,
                v.Asset.Description,
                v.Asset.Price,
                v.Asset.DeletedAt,
                v.VersionNumber,
                v.CreatedAt,
                v.FileName,
                v.StorageKey,
                v.ContentLength,
                v.ContentSha256,
                v.LicenseCode.ToString(),
                v.LicenseTemplateVersion,
                v.LicenseDisplayName,
                v.LicenseTerms))
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

        var isAuthor = requesterUserId.HasValue
            && await dbContext.Assets.AsNoTracking()
                .AnyAsync(a => a.Id == assetId && a.AuthorId == requesterUserId.Value, cancellationToken);

        // Active (non-deleted) listings expose version history publicly.
        // Soft-deleted assets require author or entitled purchaser.
        if (includeDeletedAsset)
        {
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

        IQueryable<AssetVersion> versionQuery = dbContext.AssetVersions
            .AsNoTracking()
            .Where(v => v.AssetId == assetId);

        // Non-authors only see READY versions in the version history
        if (!isAuthor)
        {
            versionQuery = versionQuery.Where(v => v.ProcessingStatus == AssetVersionProcessingStatus.READY);
        }

        return await versionQuery
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
                    v.LicenseTerms),
                v.ProcessingStatus,
                v.ProcessingErrorCode,
                v.ProcessingErrorSummary,
                v.ProcessingUpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<AssetVersion> CreateNextCandidateVersion(Guid assetId, Guid authorId, AssetVersion draft, CancellationToken cancellationToken = default)
    {
        // Row lock to prevent concurrent publishes on the same asset.
        var asset = await GetForUpdate(assetId, cancellationToken)
            ?? throw new Domain.Core.Exceptions.AssetNotFoundException();

        if (asset.DeletedAt.HasValue)
        {
            throw new Domain.Core.Exceptions.AssetNotFoundException();
        }

        if (asset.AuthorId != authorId)
        {
            throw new UnauthorizedAccessException($"User {authorId} is not the author of asset {assetId}.");
        }

        var maxVersion = await dbContext.AssetVersions
            .Where(v => v.AssetId == assetId)
            .MaxAsync(v => (int?)v.VersionNumber, cancellationToken) ?? 0;

        var dbNow = await dbContext.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT clock_timestamp() AS "Value" """).FirstAsync(cancellationToken);

        draft.AssetId = assetId;
        draft.VersionNumber = maxVersion + 1;
        draft.IsCurrent = false;
        draft.ProcessingStatus = AssetVersionProcessingStatus.PENDING_INSPECTION;
        draft.ProcessingUpdatedAt = dbNow;
        if (draft.CreatedAt == default)
        {
            draft.CreatedAt = dbNow;
        }

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
        // Public catalog query: ALWAYS requires asset to have a current READY version.
        IQueryable<Asset> query = dbContext.Assets.AsNoTracking()
            .Where(a => a.DeletedAt == null
                && a.Versions.Any(v => v.IsCurrent && v.ProcessingStatus == AssetVersionProcessingStatus.READY));

        return await QueryPagedAssets(query, request, cancellationToken);
    }

    public async Task<PagedResult<SellerAssetListItem>> GetMyListings(Guid authorId, GetAssetsRequest request, CancellationToken cancellationToken = default)
    {
        // Authenticated seller listings: scoped to the authenticated author, includes pending/processing versions.
        IQueryable<Asset> query = dbContext.Assets.AsNoTracking()
            .Where(a => a.DeletedAt == null && a.AuthorId == authorId);

        query = ApplyAssetListFilters(query, request);
        var totalCount = await query.CountAsync(cancellationToken);
        query = ApplyAssetListSort(query, request);
        var (page, pageSize) = NormalizePaging(request);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new SellerAssetListItem(
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
                a.Reviews.Average(r => (double?)r.Rating) ?? 0d,
                a.Versions.OrderByDescending(v => v.VersionNumber).Select(v => v.Id).FirstOrDefault(),
                a.Versions.OrderByDescending(v => v.VersionNumber).Select(v => v.VersionNumber).FirstOrDefault(),
                a.Versions
                    .Where(v => v.IsCurrent && v.ProcessingStatus == AssetVersionProcessingStatus.READY)
                    .Select(v => (Guid?)v.Id)
                    .FirstOrDefault(),
                a.Versions.OrderByDescending(v => v.VersionNumber).Select(v => v.ProcessingStatus).FirstOrDefault(),
                a.Versions.OrderByDescending(v => v.VersionNumber).Select(v => v.ProcessingUpdatedAt).FirstOrDefault(),
                a.Versions.OrderByDescending(v => v.VersionNumber).Select(v => v.ProcessingErrorCode).FirstOrDefault(),
                a.Versions.OrderByDescending(v => v.VersionNumber).Select(v => v.ProcessingErrorSummary).FirstOrDefault()))
            .ToListAsync(cancellationToken);

        return new PagedResult<SellerAssetListItem>(items, totalCount, page, pageSize);
    }

    public async Task<SellerAssetDetailItem?> GetOwnedSellerDetail(
        Guid assetId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Assets.AsNoTracking()
            .Where(a => a.Id == assetId && a.AuthorId == ownerUserId && a.DeletedAt == null && a.Versions.Any())
            .Select(a => new SellerAssetDetailItem(
                a.Id,
                a.Title,
                a.Description,
                a.Price,
                a.CategoryId,
                a.Category.Name,
                a.AuthorId,
                a.Author.Username,
                a.CreatedAt,
                a.UpdatedAt,
                a.AssetTags
                    .Select(at => at.Tag.Name)
                    .OrderBy(n => n)
                    .ToList(),
                a.Versions.OrderByDescending(v => v.VersionNumber).Select(v => v.Id).First(),
                a.Versions.OrderByDescending(v => v.VersionNumber).Select(v => v.VersionNumber).First(),
                a.Versions
                    .Where(v => v.IsCurrent && v.ProcessingStatus == AssetVersionProcessingStatus.READY)
                    .Select(v => (Guid?)v.Id)
                    .FirstOrDefault(),
                a.Versions.OrderByDescending(v => v.VersionNumber).Select(v => v.ProcessingStatus).First(),
                a.Versions.OrderByDescending(v => v.VersionNumber).Select(v => v.ProcessingUpdatedAt).First(),
                a.Versions.OrderByDescending(v => v.VersionNumber).Select(v => v.ProcessingErrorCode).FirstOrDefault(),
                a.Versions.OrderByDescending(v => v.VersionNumber).Select(v => v.ProcessingErrorSummary).FirstOrDefault()))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<PagedResult<AssetListItem>> QueryPagedAssets(
        IQueryable<Asset> baseQuery,
        GetAssetsRequest request,
        CancellationToken cancellationToken)
    {
        var query = ApplyAssetListFilters(baseQuery, request);
        var totalCount = await query.CountAsync(cancellationToken);
        query = ApplyAssetListSort(query, request);
        var (page, pageSize) = NormalizePaging(request);

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

    private static IQueryable<Asset> ApplyAssetListFilters(IQueryable<Asset> query, GetAssetsRequest request)
    {
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

        return query;
    }

    private static IQueryable<Asset> ApplyAssetListSort(IQueryable<Asset> query, GetAssetsRequest request)
    {
        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) || !GetAssetsRequest.AllowedSortBy.Contains(request.SortBy)
            ? "CreatedAt"
            : request.SortBy.Trim();
        var sortKey = sortBy.ToUpperInvariant();
        var isDesc = request.SortDirection == SortDirection.DESC;

        return sortKey switch
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
    }

    private static (int Page, int PageSize) NormalizePaging(GetAssetsRequest request)
    {
        var page = Math.Max(PagedRequest.DEFAULT_PAGE, request.Page);
        var pageSize = Math.Clamp(request.PageSize, PagedRequest.MIN_PAGE_SIZE, PagedRequest.MAX_PAGE_SIZE);
        return (page, pageSize);
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

        var local = dbContext.Assets.Local.FirstOrDefault(a => a.Id == id);
        if (local is not null)
        {
            local.DeletedAt = deletedAt;
            local.UpdatedAt = deletedAt;
        }
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

    public Task<Guid?> GetPublicAnalyticsSellerId(Guid assetId, CancellationToken cancellationToken = default)
    {
        return dbContext.Assets
            .AsNoTracking()
            .Where(a => a.Id == assetId && a.DeletedAt == null)
            .Select(a => (Guid?)a.AuthorId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<Guid?> ResolveDownloadAnalyticsSellerId(
        Guid assetId,
        Guid assetVersionId,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        return (
            from asset in dbContext.Assets.AsNoTracking()
            // Soft-deleted assets remain downloadable for entitled buyers; public view projections
            // still exclude them via GetPublicAnalyticsSellerId.
            where asset.Id == assetId && asset.AuthorId != actorUserId
            from requestedVersion in dbContext.AssetVersions.AsNoTracking()
                .Where(v => v.AssetId == assetId && v.Id == assetVersionId)
            from purchase in dbContext.Purchases.AsNoTracking()
                .Where(p => p.UserId == actorUserId && p.AssetId == assetId)
            from purchasedVersion in dbContext.AssetVersions.AsNoTracking()
                .Where(v => v.AssetId == assetId && v.Id == purchase.AssetVersionId)
            where requestedVersion.VersionNumber >= purchasedVersion.VersionNumber
                  && requestedVersion.ProcessingStatus == AssetVersionProcessingStatus.READY
            select (Guid?)asset.AuthorId
        ).FirstOrDefaultAsync(cancellationToken);
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", @"\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
