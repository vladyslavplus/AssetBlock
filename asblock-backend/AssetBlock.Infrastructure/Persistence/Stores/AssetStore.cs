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
            foreach (Tag tag in tags)
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
            foreach (Tag tag in tags)
            {
                asset.AssetTags.Add(new AssetTag
                {
                    AssetId = asset.Id,
                    TagId = tag.Id
                });
            }
        }

        DateTimeOffset dbNow = await dbContext.Database.SqlQueryRaw<DateTimeOffset>(
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
        => GetById(id, includeDeleted: false, cancellationToken);

    public Task<Asset?> GetById(Guid id, bool includeDeleted, CancellationToken cancellationToken = default)
    {
        IQueryable<Asset> query = dbContext.Assets.AsNoTracking();
        if (includeDeleted)
        {
            query = query.IgnoreQueryFilters();
        }

        return query
            .AsSplitQuery()
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
        var lockedId = await dbContext.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM assets WHERE "Id" = {id} FOR UPDATE""")
            .FirstOrDefaultAsync(cancellationToken);

        if (lockedId == Guid.Empty)
        {
            return null;
        }

        return await dbContext.Assets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    public Task<AssetCurrentVersionSnapshot?> GetCurrentVersionSnapshot(Guid assetId, CancellationToken cancellationToken = default)
    {
        return dbContext.AssetVersions
            .IgnoreQueryFilters()
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
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.AssetId == assetId && v.Id == versionId, cancellationToken);
    }

    public async Task<AssetOwnershipDto?> GetOwnership(Guid assetId, CancellationToken cancellationToken = default)
    {
        return await dbContext.Assets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.Id == assetId)
            .Select(a => new AssetOwnershipDto(a.Id, a.AuthorId, a.DeletedAt != null))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AssetVersionSummaryDto>?> ListVersions(
        Guid assetId,
        Guid? requesterUserId,
        CancellationToken cancellationToken = default)
    {
        var result = await dbContext.Assets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.Id == assetId)
            .Select(a => new
            {
                IsDeleted = a.DeletedAt != null,
                IsAuthor = requesterUserId.HasValue && a.AuthorId == requesterUserId.Value,
                HasPurchased = requesterUserId.HasValue && dbContext.Purchases
                    .Any(p => p.AssetId == a.Id && p.UserId == requesterUserId.Value),
                Versions = a.Versions
                    .Where(v => (requesterUserId.HasValue && a.AuthorId == requesterUserId.Value)
                                || v.ProcessingStatus == AssetVersionProcessingStatus.READY)
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
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (result is null)
        {
            return null;
        }

        if (result.IsDeleted && !result.IsAuthor && !result.HasPurchased)
        {
            return null;
        }

        return result.Versions;
    }

    public async Task<AssetVersion> CreateNextCandidateVersion(Guid assetId, Guid authorId, AssetVersion draft, CancellationToken cancellationToken = default)
    {
        // Row lock to prevent concurrent publishes on the same asset.
        Asset asset = await GetForUpdate(assetId, cancellationToken)
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

        DateTimeOffset dbNow = await dbContext.Database.SqlQueryRaw<DateTimeOffset>(
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
        (int page, int pageSize) = NormalizePaging(request);

        List<SellerAssetListItem> items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new
            {
                a.Id,
                a.Title,
                a.Description,
                a.Price,
                a.CategoryId,
                CategoryName = a.Category.Name,
                a.AuthorId,
                AuthorUsername = a.Author.Username,
                a.CreatedAt,
                Tags = a.AssetTags
                    .Select(at => at.Tag.Name)
                    .OrderBy(n => n)
                    .ToList(),
                AverageRating = a.RatingAverage,
                LatestVersion = a.Versions
                    .OrderByDescending(v => v.VersionNumber)
                    .Select(v => new
                    {
                        v.Id,
                        v.VersionNumber,
                        v.ProcessingStatus,
                        v.ProcessingUpdatedAt,
                        v.ProcessingErrorCode,
                        v.ProcessingErrorSummary
                    })
                    .FirstOrDefault(),
                CurrentReadyVersionId = a.Versions
                    .Where(v => v.IsCurrent && v.ProcessingStatus == AssetVersionProcessingStatus.READY)
                    .Select(v => (Guid?)v.Id)
                    .FirstOrDefault()
            })
            .Select(x => new SellerAssetListItem(
                x.Id,
                x.Title,
                x.Description,
                x.Price,
                x.CategoryId,
                x.CategoryName,
                x.AuthorId,
                x.AuthorUsername,
                x.CreatedAt,
                x.Tags,
                x.AverageRating,
                x.LatestVersion != null ? x.LatestVersion.Id : Guid.Empty,
                x.LatestVersion != null ? x.LatestVersion.VersionNumber : 0,
                x.CurrentReadyVersionId,
                x.LatestVersion != null ? x.LatestVersion.ProcessingStatus : AssetVersionProcessingStatus.PENDING_INSPECTION,
                x.LatestVersion != null ? x.LatestVersion.ProcessingUpdatedAt : default,
                x.LatestVersion != null ? x.LatestVersion.ProcessingErrorCode : null,
                x.LatestVersion != null ? x.LatestVersion.ProcessingErrorSummary : null))
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
            .Select(a => new
            {
                a.Id,
                a.Title,
                a.Description,
                a.Price,
                a.CategoryId,
                CategoryName = a.Category.Name,
                a.AuthorId,
                AuthorUsername = a.Author.Username,
                a.CreatedAt,
                a.UpdatedAt,
                Tags = a.AssetTags
                    .Select(at => at.Tag.Name)
                    .OrderBy(n => n)
                    .ToList(),
                LatestVersion = a.Versions
                    .OrderByDescending(v => v.VersionNumber)
                    .Select(v => new
                    {
                        v.Id,
                        v.VersionNumber,
                        v.ProcessingStatus,
                        v.ProcessingUpdatedAt,
                        v.ProcessingErrorCode,
                        v.ProcessingErrorSummary
                    })
                    .FirstOrDefault(),
                CurrentReadyVersionId = a.Versions
                    .Where(v => v.IsCurrent && v.ProcessingStatus == AssetVersionProcessingStatus.READY)
                    .Select(v => (Guid?)v.Id)
                    .FirstOrDefault()
            })
            .Select(x => new SellerAssetDetailItem(
                x.Id,
                x.Title,
                x.Description,
                x.Price,
                x.CategoryId,
                x.CategoryName,
                x.AuthorId,
                x.AuthorUsername,
                x.CreatedAt,
                x.UpdatedAt,
                x.Tags,
                x.LatestVersion != null ? x.LatestVersion.Id : Guid.Empty,
                x.LatestVersion != null ? x.LatestVersion.VersionNumber : 0,
                x.CurrentReadyVersionId,
                x.LatestVersion != null ? x.LatestVersion.ProcessingStatus : AssetVersionProcessingStatus.PENDING_INSPECTION,
                x.LatestVersion != null ? x.LatestVersion.ProcessingUpdatedAt : default,
                x.LatestVersion != null ? x.LatestVersion.ProcessingErrorCode : null,
                x.LatestVersion != null ? x.LatestVersion.ProcessingErrorSummary : null))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<PagedResult<AssetListItem>> QueryPagedAssets(
        IQueryable<Asset> baseQuery,
        GetAssetsRequest request,
        CancellationToken cancellationToken)
    {
        (int page, int pageSize) = NormalizePaging(request);
        IQueryable<Asset> filteredBase = ApplyNonSearchFilters(baseQuery, request);

        if (string.IsNullOrWhiteSpace(request.Search))
        {
            var totalCount = await filteredBase.CountAsync(cancellationToken);
            if (totalCount == 0 || (page - 1) * pageSize >= totalCount)
            {
                return new PagedResult<AssetListItem>([], totalCount, page, pageSize);
            }

            IQueryable<Asset> sortedQuery = ApplyAssetListSort(filteredBase, request);
            List<AssetListItem> items = await sortedQuery
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
                    a.RatingAverage))
                .ToListAsync(cancellationToken);

            return new PagedResult<AssetListItem>(items, totalCount, page, pageSize);
        }

        return await QueryPagedSearchedCatalog(filteredBase, request, page, pageSize, cancellationToken);
    }

    private static async Task<PagedResult<AssetListItem>> QueryPagedSearchedCatalog(
        IQueryable<Asset> filteredBase,
        GetAssetsRequest request,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var searchText = request.Search!.Trim();
        var exactTitlePattern = EscapeLikePattern(searchText);
        var likePattern = $"%{exactTitlePattern}%";
        var isLongEnoughForTrigram = searchText.Length >= MIN_TRIGRAM_QUERY_LENGTH;
        var candidateLimit = page * pageSize;

        IQueryable<Guid> ftsMatchingIds = filteredBase
            .Where(a => EF.Property<NpgsqlTsVector>(a, AssetConfiguration.SEARCH_VECTOR_PROPERTY)
                .Matches(EF.Functions.WebSearchToTsQuery("simple", searchText)))
            .Select(a => a.Id);

        IQueryable<Guid> titleIlikeMatchingIds = filteredBase
            .Where(a => EF.Functions.ILike(a.Title, likePattern, LIKE_ESCAPE))
            .Select(a => a.Id);

        IQueryable<Guid> descIlikeMatchingIds = filteredBase
            .Where(a => a.Description != null && EF.Functions.ILike(a.Description, likePattern, LIKE_ESCAPE))
            .Select(a => a.Id);

        IQueryable<Guid> allMatchingIds = ftsMatchingIds
            .Union(titleIlikeMatchingIds)
            .Union(descIlikeMatchingIds);

        if (isLongEnoughForTrigram)
        {
            IQueryable<Guid> titleTrgmMatchingIds = filteredBase
                .Where(a => EF.Functions.TrigramsAreSimilar(a.Title, searchText)
                    && PostgresDbFunctions.TrigramsSimilarity(a.Title, searchText) >= TRIGRAM_SIMILARITY_THRESHOLD)
                .Select(a => a.Id);

            IQueryable<Guid> descTrgmMatchingIds = filteredBase
                .Where(a => a.Description != null
                    && EF.Functions.TrigramsAreSimilar(a.Description, searchText)
                    && PostgresDbFunctions.TrigramsSimilarity(a.Description, searchText) >= TRIGRAM_SIMILARITY_THRESHOLD)
                .Select(a => a.Id);

            allMatchingIds = allMatchingIds
                .Union(titleTrgmMatchingIds)
                .Union(descTrgmMatchingIds);
        }

        var totalCount = await allMatchingIds.CountAsync(cancellationToken);
        if (totalCount == 0 || (page - 1) * pageSize >= totalCount)
        {
            return new PagedResult<AssetListItem>([], totalCount, page, pageSize);
        }

        var hasExplicitSort = !string.IsNullOrWhiteSpace(request.SortBy)
            && GetAssetsRequest.AllowedSortBy.Contains(request.SortBy);

        List<Guid> pageAssetIds;

        if (hasExplicitSort)
        {
            pageAssetIds = await FetchExplicitSortedPageAssetIds(
                filteredBase,
                request,
                searchText,
                likePattern,
                isLongEnoughForTrigram,
                candidateLimit,
                page,
                pageSize,
                cancellationToken);
        }
        else
        {
            pageAssetIds = await FetchRelevanceRankedPageAssetIds(
                filteredBase,
                searchText,
                exactTitlePattern,
                likePattern,
                isLongEnoughForTrigram,
                candidateLimit,
                page,
                pageSize,
                cancellationToken);
        }

        if (pageAssetIds.Count == 0)
        {
            return new PagedResult<AssetListItem>([], totalCount, page, pageSize);
        }

        List<AssetListItem> items = await filteredBase
            .Where(a => pageAssetIds.Contains(a.Id))
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
                a.RatingAverage))
            .ToListAsync(cancellationToken);

        var orderMap = pageAssetIds.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        items.Sort((a, b) => orderMap[a.Id].CompareTo(orderMap[b.Id]));

        return new PagedResult<AssetListItem>(items, totalCount, page, pageSize);
    }

    private static async Task<List<Guid>> FetchRelevanceRankedPageAssetIds(
        IQueryable<Asset> filteredBase,
        string searchText,
        string exactTitlePattern,
        string likePattern,
        bool isLongEnoughForTrigram,
        int candidateLimit,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var ftsBranch = filteredBase
            .Where(a => EF.Property<NpgsqlTsVector>(a, AssetConfiguration.SEARCH_VECTOR_PROPERTY)
                .Matches(EF.Functions.WebSearchToTsQuery("simple", searchText)))
            .Select(a => new
            {
                a.Id,
                a.CreatedAt,
                Score = 100.0f
                    + (EF.Functions.ILike(a.Title, exactTitlePattern, LIKE_ESCAPE) ? 50.0f : (EF.Functions.ILike(a.Title, likePattern, LIKE_ESCAPE) ? 20.0f : 0.0f))
                    + (EF.Property<NpgsqlTsVector>(a, AssetConfiguration.SEARCH_VECTOR_PROPERTY).Rank(EF.Functions.WebSearchToTsQuery("simple", searchText)) * 10.0f)
            })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(candidateLimit);

        var titleIlikeBranch = filteredBase
            .Where(a => EF.Functions.ILike(a.Title, likePattern, LIKE_ESCAPE))
            .Select(a => new
            {
                a.Id,
                a.CreatedAt,
                Score = 40.0f
                    + (EF.Functions.ILike(a.Title, exactTitlePattern, LIKE_ESCAPE) ? 30.0f : 0.0f)
                    + (isLongEnoughForTrigram ? PostgresDbFunctions.TrigramsSimilarity(a.Title, searchText) * 10.0f : 0.0f)
            })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(candidateLimit);

        var descIlikeBranch = filteredBase
            .Where(a => a.Description != null && EF.Functions.ILike(a.Description, likePattern, LIKE_ESCAPE))
            .Select(a => new
            {
                a.Id,
                a.CreatedAt,
                Score = 15.0f
                    + (isLongEnoughForTrigram ? PostgresDbFunctions.TrigramsSimilarity(a.Description!, searchText) * 5.0f : 0.0f)
            })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(candidateLimit);

        var allCandidates = ftsBranch
            .Concat(titleIlikeBranch)
            .Concat(descIlikeBranch);

        if (isLongEnoughForTrigram)
        {
            var titleTrgmBranch = filteredBase
                .Where(a => EF.Functions.TrigramsAreSimilar(a.Title, searchText)
                    && PostgresDbFunctions.TrigramsSimilarity(a.Title, searchText) >= TRIGRAM_SIMILARITY_THRESHOLD)
                .Select(a => new
                {
                    a.Id,
                    a.CreatedAt,
                    Score = 5.0f + (PostgresDbFunctions.TrigramsSimilarity(a.Title, searchText) * 20.0f)
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .Take(candidateLimit);

            var descTrgmBranch = filteredBase
                .Where(a => a.Description != null
                    && EF.Functions.TrigramsAreSimilar(a.Description, searchText)
                    && PostgresDbFunctions.TrigramsSimilarity(a.Description, searchText) >= TRIGRAM_SIMILARITY_THRESHOLD)
                .Select(a => new
                {
                    a.Id,
                    a.CreatedAt,
                    Score = 1.0f + (PostgresDbFunctions.TrigramsSimilarity(a.Description!, searchText) * 5.0f)
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .Take(candidateLimit);

            allCandidates = allCandidates
                .Concat(titleTrgmBranch)
                .Concat(descTrgmBranch);
        }

        var deduplicated = allCandidates
            .GroupBy(x => new { x.Id, x.CreatedAt })
            .Select(g => new
            {
                g.Key.Id,
                g.Key.CreatedAt,
                Score = g.Max(x => x.Score)
            });

        return await deduplicated
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    private static async Task<List<Guid>> FetchExplicitSortedPageAssetIds(
        IQueryable<Asset> filteredBase,
        GetAssetsRequest request,
        string searchText,
        string likePattern,
        bool isLongEnoughForTrigram,
        int candidateLimit,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var sortBy = request.SortBy!.Trim().ToUpperInvariant();
        var isDesc = request.SortDirection == SortDirection.DESC;

        var ftsBranch = BoundSortedBranch(
            filteredBase.Where(a => EF.Property<NpgsqlTsVector>(a, AssetConfiguration.SEARCH_VECTOR_PROPERTY)
                .Matches(EF.Functions.WebSearchToTsQuery("simple", searchText))),
            sortBy,
            isDesc,
            candidateLimit);

        var titleIlikeBranch = BoundSortedBranch(
            filteredBase.Where(a => EF.Functions.ILike(a.Title, likePattern, LIKE_ESCAPE)),
            sortBy,
            isDesc,
            candidateLimit);

        var descIlikeBranch = BoundSortedBranch(
            filteredBase.Where(a => a.Description != null && EF.Functions.ILike(a.Description, likePattern, LIKE_ESCAPE)),
            sortBy,
            isDesc,
            candidateLimit);

        var mergedCandidateIds = ftsBranch
            .Union(titleIlikeBranch)
            .Union(descIlikeBranch);

        if (isLongEnoughForTrigram)
        {
            var titleTrgmBranch = BoundSortedBranch(
                filteredBase.Where(a => EF.Functions.TrigramsAreSimilar(a.Title, searchText)
                    && PostgresDbFunctions.TrigramsSimilarity(a.Title, searchText) >= TRIGRAM_SIMILARITY_THRESHOLD),
                sortBy,
                isDesc,
                candidateLimit);

            var descTrgmBranch = BoundSortedBranch(
                filteredBase.Where(a => a.Description != null
                    && EF.Functions.TrigramsAreSimilar(a.Description, searchText)
                    && PostgresDbFunctions.TrigramsSimilarity(a.Description, searchText) >= TRIGRAM_SIMILARITY_THRESHOLD),
                sortBy,
                isDesc,
                candidateLimit);

            mergedCandidateIds = mergedCandidateIds
                .Union(titleTrgmBranch)
                .Union(descTrgmBranch);
        }

        var candidateAssets = filteredBase.Where(a => mergedCandidateIds.Contains(a.Id));

        IQueryable<Asset> sorted = sortBy switch
        {
            "TITLE" => isDesc
                ? candidateAssets.OrderByDescending(a => a.Title).ThenBy(a => a.Id)
                : candidateAssets.OrderBy(a => a.Title).ThenBy(a => a.Id),
            "PRICE" => isDesc
                ? candidateAssets.OrderByDescending(a => a.Price).ThenBy(a => a.Id)
                : candidateAssets.OrderBy(a => a.Price).ThenBy(a => a.Id),
            "ID" => isDesc
                ? candidateAssets.OrderByDescending(a => a.Id)
                : candidateAssets.OrderBy(a => a.Id),
            _ => isDesc
                ? candidateAssets.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id)
                : candidateAssets.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
        };

        return await sorted
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);
    }

    private static IQueryable<Guid> BoundSortedBranch(
        IQueryable<Asset> branch,
        string sortBy,
        bool isDesc,
        int candidateLimit)
    {
        IQueryable<Asset> sorted = sortBy switch
        {
            "TITLE" => isDesc
                ? branch.OrderByDescending(a => a.Title).ThenBy(a => a.Id)
                : branch.OrderBy(a => a.Title).ThenBy(a => a.Id),
            "PRICE" => isDesc
                ? branch.OrderByDescending(a => a.Price).ThenBy(a => a.Id)
                : branch.OrderBy(a => a.Price).ThenBy(a => a.Id),
            "ID" => isDesc
                ? branch.OrderByDescending(a => a.Id)
                : branch.OrderBy(a => a.Id),
            _ => isDesc
                ? branch.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id)
                : branch.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
        };

        return sorted.Take(candidateLimit).Select(a => a.Id);
    }

    private static IQueryable<Asset> ApplyNonSearchFilters(IQueryable<Asset> query, GetAssetsRequest request)
    {
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

    private static IQueryable<Asset> ApplyAssetListFilters(IQueryable<Asset> query, GetAssetsRequest request)
    {
        query = ApplyNonSearchFilters(query, request);

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
                    || (EF.Functions.TrigramsAreSimilar(a.Title, searchText)
                        && PostgresDbFunctions.TrigramsSimilarity(a.Title, searchText) >= TRIGRAM_SIMILARITY_THRESHOLD)
                    || (a.Description != null
                        && EF.Functions.TrigramsAreSimilar(a.Description, searchText)
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

        Asset? local = dbContext.Assets.Local.FirstOrDefault(a => a.Id == id);
        if (local is not null)
        {
            local.DeletedAt = deletedAt;
            local.UpdatedAt = deletedAt;
        }
    }

    public async Task Delete(Guid id, CancellationToken cancellationToken = default)
    {
        await dbContext.Assets.IgnoreQueryFilters().Where(a => a.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task AddTag(Guid assetId, Guid tagId, CancellationToken cancellationToken = default)
    {
        await TryAddTag(assetId, tagId, cancellationToken);
    }

    public async Task<bool> TryAddTag(Guid assetId, Guid tagId, CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO asset_tags (\"AssetId\", \"TagId\") VALUES ({assetId}, {tagId}) ON CONFLICT (\"AssetId\", \"TagId\") DO NOTHING",
            cancellationToken);

        return rows > 0;
    }

    public Task<bool> HasAssetTag(Guid assetId, Guid tagId, CancellationToken cancellationToken = default)
    {
        return dbContext.Set<AssetTag>()
            .AsNoTracking()
            .AnyAsync(at => at.AssetId == assetId && at.TagId == tagId, cancellationToken);
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
        Asset? asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, cancellationToken);
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
        return dbContext.Assets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.Id == assetId
                && a.AuthorId != actorUserId
                && dbContext.AssetVersions.Any(rv =>
                    rv.AssetId == assetId
                    && rv.Id == assetVersionId
                    && rv.ProcessingStatus == AssetVersionProcessingStatus.READY
                    && dbContext.Purchases.Any(p =>
                        p.UserId == actorUserId
                        && p.AssetId == assetId
                        && dbContext.AssetVersions.Any(pv =>
                            pv.AssetId == assetId
                            && pv.Id == p.AssetVersionId
                            && rv.VersionNumber >= pv.VersionNumber))))
            .Select(a => (Guid?)a.AuthorId)
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
