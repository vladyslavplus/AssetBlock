using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NpgsqlTypes;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class AssetStore(
    ApplicationDbContext dbContext,
    IAssetProcessingJobStore? jobStore = null,
    IOptions<EmbeddingOptions>? embeddingOptions = null,
    TimeProvider? timeProvider = null,
    IRecommendationPersonalizationStore? personalizationStore = null) : IAssetStore
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
        if (!includeDeleted)
        {
            query = query.Where(a => a.DeletedAt == null);
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
        Guid lockedId = await dbContext.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM assets WHERE "Id" = {id} FOR UPDATE""")
            .FirstOrDefaultAsync(cancellationToken);

        if (lockedId == Guid.Empty)
        {
            return null;
        }

        return await dbContext.Assets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
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

    public async Task<AssetOwnershipDto?> GetOwnership(Guid assetId, CancellationToken cancellationToken = default)
    {
        return await dbContext.Assets
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

    public async Task<CatalogPageResult<AssetListItem>> GetPaged(
        GetAssetsRequest request,
        float[]? queryEmbedding = null,
        string? modelKey = null,
        CancellationToken cancellationToken = default)
    {
        // Public catalog query: ALWAYS requires asset to have a current READY version.
        IQueryable<Asset> query = dbContext.Assets.AsNoTracking()
            .Where(a => a.DeletedAt == null
                && a.Versions.Any(v => v.IsCurrent && v.ProcessingStatus == AssetVersionProcessingStatus.READY));

        return await QueryPagedAssets(query, request, queryEmbedding, modelKey, cancellationToken);
    }

    public async Task<SimilarPublicAssetsResult?> GetSimilarPublic(
        Guid sourceAssetId,
        int limit,
        SimilarAssetsQueryOptions options,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, SimilarAssetsConstants.MIN_LIMIT, SimilarAssetsConstants.MAX_LIMIT);

        var source = await PublicVisibleAssets()
            .Where(a => a.Id == sourceAssetId)
            .Select(a => new
            {
                a.Id,
                a.CategoryId,
                a.SearchRevision,
                TagIds = a.AssetTags.Select(at => at.TagId).Distinct().ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (source is null)
        {
            return null;
        }

        List<Guid> sourceTagIds = source.TagIds;
        var sourceTagCount = sourceTagIds.Count;

        var rankedRows = await PublicVisibleAssets()
            .Where(a => a.Id != sourceAssetId && a.CategoryId == source.CategoryId)
            .Select(a => new
            {
                a.Id,
                a.RatingAverage,
                a.RatingCount,
                a.SearchRevision,
                CandidateTagCount = a.AssetTags.Count(),
                IntersectCount = a.AssetTags.Count(at => sourceTagIds.Contains(at.TagId))
            })
            .Select(x => new
            {
                x.Id,
                x.RatingAverage,
                x.RatingCount,
                x.SearchRevision,
                x.IntersectCount,
                Jaccard = x.CandidateTagCount + sourceTagCount == x.IntersectCount
                    ? 0d
                    : x.IntersectCount * 1.0 / (x.CandidateTagCount + sourceTagCount - x.IntersectCount)
            })
            .OrderByDescending(x => x.Jaccard)
            .ThenByDescending(x => x.RatingAverage)
            .ThenByDescending(x => x.RatingCount)
            .ThenBy(x => x.Id)
            .Take(SimilarAssetsConstants.SHORTLIST_SIZE)
            .ToListAsync(cancellationToken);

        var shortlist = rankedRows
            .Select(x => new SimilarShortlistRow(
                x.Id,
                x.RatingAverage,
                x.RatingCount,
                x.SearchRevision,
                x.Jaccard,
                x.IntersectCount))
            .ToList();

        SimilarRankedShortlist ranked;
        var usedPersonalization = false;
        HashSet<Guid> popularitySignaled;
        HashSet<Guid> personalClicks = [];
        HashSet<Guid> personalTags = [];
        if (options.PersonalUserId is { } personalUserId)
        {
            (ranked, usedPersonalization, personalClicks, personalTags, popularitySignaled) = await ApplyPersonalRanking(
                personalUserId,
                sourceAssetId,
                shortlist,
                () => RankPhaseA(sourceAssetId, source.SearchRevision, shortlist, options, cancellationToken),
                cancellationToken);
        }
        else
        {
            (ranked, popularitySignaled) = await RankPhaseA(sourceAssetId, source.SearchRevision, shortlist, options, cancellationToken);
        }

        var selectedIds = ranked.Rows
            .Select(r => r.Id)
            .Distinct()
            .Take(limit)
            .ToList();

        var sourceStillVisible = await PublicVisibleAssets()
            .AnyAsync(a => a.Id == sourceAssetId, cancellationToken);
        if (!sourceStillVisible)
        {
            return null;
        }

        if (selectedIds.Count == 0)
        {
            return new SimilarPublicAssetsResult(
                [],
                ranked.UsedSemanticRefinement,
                usedPersonalization,
                new Dictionary<Guid, SimilarCandidateEvidence>());
        }

        List<AssetListItem> hydrated = await PublicVisibleAssets()
            .Where(a => selectedIds.Contains(a.Id))
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

        var byId = hydrated.ToDictionary(i => i.Id);
        var sharedTagsById = shortlist
            .GroupBy(r => r.Id)
            .ToDictionary(g => g.Key, g => g.First().IntersectCount);
        var evidence = selectedIds
            .Distinct()
            .ToDictionary(
                id => id,
                id => new SimilarCandidateEvidence(
                    personalClicks.Contains(id),
                    personalTags.Contains(id),
                    popularitySignaled.Contains(id),
                    sharedTagsById.GetValueOrDefault(id)));
        return new SimilarPublicAssetsResult(
            selectedIds
                .Where(byId.ContainsKey)
                .Select(id => byId[id])
                .ToList(),
            ranked.UsedSemanticRefinement,
            usedPersonalization,
            evidence);
    }

    public async Task<PagedResult<SellerAssetListItem>> GetMyListings(Guid authorId, GetAssetsRequest request, CancellationToken cancellationToken = default)
    {
        // Authenticated seller listings: scoped to the authenticated author, includes pending/processing versions.
        IQueryable<Asset> query = dbContext.Assets.AsNoTracking()
            .Where(a => a.DeletedAt == null && a.AuthorId == authorId);

        query = ApplyAssetListFilters(query, request);
        var totalCount = await query.CountAsync(cancellationToken);
        query = ApplyAssetListSort(query, request);
        (var page, var pageSize) = NormalizePaging(request);

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

    private async Task<CatalogPageResult<AssetListItem>> QueryPagedAssets(
        IQueryable<Asset> baseQuery,
        GetAssetsRequest request,
        float[]? queryEmbedding,
        string? modelKey,
        CancellationToken cancellationToken)
    {
        (var page, var pageSize) = NormalizePaging(request);
        IQueryable<Asset> filteredBase = ApplyNonSearchFilters(baseQuery, request);

        if (string.IsNullOrWhiteSpace(request.Search))
        {
            var totalCount = await filteredBase.CountAsync(cancellationToken);
            if (totalCount == 0 || (page - 1) * pageSize >= totalCount)
            {
                return new CatalogPageResult<AssetListItem>([], totalCount, page, pageSize);
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

            return new CatalogPageResult<AssetListItem>(items, totalCount, page, pageSize);
        }

        var hasExplicitSort = !string.IsNullOrWhiteSpace(request.SortBy)
            && GetAssetsRequest.AllowedSortBy.Contains(request.SortBy);

        if (!hasExplicitSort && queryEmbedding is not null && !string.IsNullOrWhiteSpace(modelKey))
        {
            return await QueryPagedHybridRrfCatalog(filteredBase, request, queryEmbedding, modelKey, page, pageSize, cancellationToken);
        }

        return await QueryPagedSearchedCatalog(filteredBase, request, page, pageSize, cancellationToken);
    }

    private static async Task<CatalogPageResult<AssetListItem>> QueryPagedSearchedCatalog(
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
            return new CatalogPageResult<AssetListItem>([], totalCount, page, pageSize);
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
            return new CatalogPageResult<AssetListItem>([], totalCount, page, pageSize);
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

        return new CatalogPageResult<AssetListItem>(items, totalCount, page, pageSize);
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

        var primaryCandidates = ftsBranch
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

            primaryCandidates = primaryCandidates.Concat(titleTrgmBranch);
        }

        var primaryDeduplicated = primaryCandidates
            .GroupBy(x => new { x.Id, x.CreatedAt })
            .Select(g => new
            {
                g.Key.Id,
                g.Key.CreatedAt,
                Score = g.Max(x => x.Score)
            });

        List<Guid> primaryPageIds = await primaryDeduplicated
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        // Score floor of primary branches is >= 11.0f (title trigram min = 5.0 + 0.3*20 = 11.0f).
        // Description trigram score ceiling is <= 6.0f (1.0 + 1.0*5.0 = 6.0f).
        // If primary candidates saturate the page slice, descTrgm cannot displace any page item.
        if (primaryPageIds.Count == pageSize || !isLongEnoughForTrigram)
        {
            return primaryPageIds;
        }

        // For page 1 when underfilled: primaryPageIds contains all existing primary candidates.
        // Retrieve only the missing quota from descTrgm without re-evaluating primary branches.
        if (page == 1)
        {
            var missingCount = pageSize - primaryPageIds.Count;
            List<Guid> descTrgmPageIds = await filteredBase
                .Where(a => a.Description != null
                    && !primaryPageIds.Contains(a.Id)
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
                .Take(missingCount)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            primaryPageIds.AddRange(descTrgmPageIds);
            return primaryPageIds;
        }

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

        var allCandidates = primaryCandidates.Concat(descTrgmBranch);

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

    private async Task<CatalogPageResult<AssetListItem>> QueryPagedHybridRrfCatalog(
        IQueryable<Asset> filteredBase,
        GetAssetsRequest request,
        float[] queryEmbedding,
        string modelKey,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var searchText = request.Search!.Trim();
        var exactTitlePattern = EscapeLikePattern(searchText);
        var likePattern = $"%{exactTitlePattern}%";
        var isLongEnoughForTrigram = searchText.Length >= MIN_TRIGRAM_QUERY_LENGTH;
        const int branchLimit = 201; // 200 candidates + 1 sentinel

        // 1. Lexical branch candidates: up to 201
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
            .Take(branchLimit);

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
            .Take(branchLimit);

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
            .Take(branchLimit);

        var primaryCandidates = ftsBranch
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
                .Take(branchLimit);

            primaryCandidates = primaryCandidates.Concat(titleTrgmBranch);
        }

        var primaryDeduplicated = primaryCandidates
            .GroupBy(x => new { x.Id, x.CreatedAt })
            .Select(g => new
            {
                g.Key.Id,
                g.Key.CreatedAt,
                Score = g.Max(x => x.Score)
            });

        var lexicalRaw = await primaryDeduplicated
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(branchLimit)
            .Select(x => new { x.Id, x.CreatedAt })
            .ToListAsync(cancellationToken);

        // Score floor of primary branches is >= 11.0f vs descTrgm ceiling <= 6.0f.
        // Only evaluate descTrgm if primary branches could not fill the 201 candidate quota.
        // When underfilled, all database primary matches are already in lexicalRaw; retrieve only the
        // missing quota from descTrgm without re-evaluating primary branches.
        if (isLongEnoughForTrigram && lexicalRaw.Count < branchLimit)
        {
            var primaryIds = lexicalRaw.Select(x => x.Id).ToList();
            var missingCount = branchLimit - lexicalRaw.Count;

            var descCandidates = await filteredBase
                .Where(a => a.Description != null
                    && !primaryIds.Contains(a.Id)
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
                .Take(missingCount)
                .Select(x => new { x.Id, x.CreatedAt })
                .ToListAsync(cancellationToken);

            lexicalRaw.AddRange(descCandidates);
        }

        // 2. Semantic branch candidates: up to 201
        var targetVector = new Vector(queryEmbedding);
        var semanticRaw = await filteredBase
            .Join(
                dbContext.AssetEmbeddings.Where(e => e.ModelKey == modelKey),
                a => a.Id,
                e => e.AssetId,
                (a, e) => new { Asset = a, Embedding = e })
            .Where(x => x.Embedding.SourceRevision == x.Asset.SearchRevision)
            .Select(x => new
            {
                x.Asset.Id,
                x.Asset.CreatedAt,
                Distance = x.Embedding.Embedding.CosineDistance(targetVector)
            })
            .OrderBy(x => x.Distance)
            .ThenByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(branchLimit)
            .Select(x => new { x.Id, x.CreatedAt })
            .ToListAsync(cancellationToken);

        // 3. Sentinel detection
        var lexicalTruncated = lexicalRaw.Count > 200;
        var semanticTruncated = semanticRaw.Count > 200;
        var isTruncated = lexicalTruncated || semanticTruncated;

        var lexicalTop200 = lexicalRaw.Take(200).ToList();
        var semanticTop200 = semanticRaw.Take(200).ToList();

        // 4. RRF fusion over 1..200 ranks
        const double k = 60.0;
        var rrfMap = new Dictionary<Guid, (double rrfScore, int bestRank, DateTimeOffset createdAt)>();

        for (var i = 0; i < lexicalTop200.Count; i++)
        {
            var rank = i + 1;
            var item = lexicalTop200[i];
            var rrfContribution = 1.0 / (k + rank);
            rrfMap[item.Id] = (rrfContribution, rank, item.CreatedAt);
        }

        for (var i = 0; i < semanticTop200.Count; i++)
        {
            var rank = i + 1;
            var item = semanticTop200[i];
            var rrfContribution = 1.0 / (k + rank);

            if (rrfMap.TryGetValue(item.Id, out (double rrfScore, int bestRank, DateTimeOffset createdAt) existing))
            {
                rrfMap[item.Id] = (
                    existing.rrfScore + rrfContribution,
                    Math.Min(existing.bestRank, rank),
                    existing.createdAt);
            }
            else
            {
                rrfMap[item.Id] = (rrfContribution, rank, item.CreatedAt);
            }
        }

        // 5. Deterministic tie-breaking order:
        // RRF score desc, best non-null branch rank asc, CreatedAt desc, Id asc
        var fusedRanked = rrfMap
            .Select(kvp => new
            {
                Id = kvp.Key,
                RrfScore = kvp.Value.rrfScore,
                BestRank = kvp.Value.bestRank,
                CreatedAt = kvp.Value.createdAt
            })
            .OrderByDescending(x => x.RrfScore)
            .ThenBy(x => x.BestRank)
            .ThenByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToList();

        var totalCount = fusedRanked.Count;
        if (totalCount == 0 || (page - 1) * pageSize >= totalCount)
        {
            return new CatalogPageResult<AssetListItem>([], totalCount, page, pageSize, isTruncated);
        }

        var pageSlice = fusedRanked
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => x.Id)
            .ToList();

        List<AssetListItem> items = await filteredBase
            .Where(a => pageSlice.Contains(a.Id))
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

        var orderMap = pageSlice.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        items.Sort((a, b) => orderMap[a.Id].CompareTo(orderMap[b.Id]));

        return new CatalogPageResult<AssetListItem>(items, totalCount, page, pageSize, isTruncated);
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

        IQueryable<Guid> ftsBranch = BoundSortedBranch(
            filteredBase.Where(a => EF.Property<NpgsqlTsVector>(a, AssetConfiguration.SEARCH_VECTOR_PROPERTY)
                .Matches(EF.Functions.WebSearchToTsQuery("simple", searchText))),
            sortBy,
            isDesc,
            candidateLimit);

        IQueryable<Guid> titleIlikeBranch = BoundSortedBranch(
            filteredBase.Where(a => EF.Functions.ILike(a.Title, likePattern, LIKE_ESCAPE)),
            sortBy,
            isDesc,
            candidateLimit);

        IQueryable<Guid> descIlikeBranch = BoundSortedBranch(
            filteredBase.Where(a => a.Description != null && EF.Functions.ILike(a.Description, likePattern, LIKE_ESCAPE)),
            sortBy,
            isDesc,
            candidateLimit);

        IQueryable<Guid> mergedCandidateIds = ftsBranch
            .Union(titleIlikeBranch)
            .Union(descIlikeBranch);

        if (isLongEnoughForTrigram)
        {
            IQueryable<Guid> titleTrgmBranch = BoundSortedBranch(
                filteredBase.Where(a => EF.Functions.TrigramsAreSimilar(a.Title, searchText)
                    && PostgresDbFunctions.TrigramsSimilarity(a.Title, searchText) >= TRIGRAM_SIMILARITY_THRESHOLD),
                sortBy,
                isDesc,
                candidateLimit);

            IQueryable<Guid> descTrgmBranch = BoundSortedBranch(
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

        IQueryable<Asset> candidateAssets = filteredBase.Where(a => mergedCandidateIds.Contains(a.Id));

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
        await dbContext.Assets.Where(a => a.Id == id).ExecuteDeleteAsync(cancellationToken);
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

        if (rows > 0)
        {
            Guid? readyVersionId = await dbContext.AssetVersions
                .AsNoTracking()
                .Where(v => v.AssetId == assetId && v.IsCurrent && v.ProcessingStatus == AssetVersionProcessingStatus.READY)
                .Select(v => (Guid?)v.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (readyVersionId.HasValue)
            {
                DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
                var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE assets
                    SET "SearchRevision" = "SearchRevision" + 1,
                        "UpdatedAt" = {now}
                    WHERE "Id" = {assetId} AND "DeletedAt" IS NULL
                    """, cancellationToken);

                if (affected > 0 && jobStore != null && embeddingOptions?.Value is { Enabled: true })
                {
                    var newRevision = await dbContext.Assets
                        .AsNoTracking()
                        .Where(a => a.Id == assetId)
                        .Select(a => a.SearchRevision)
                        .FirstOrDefaultAsync(cancellationToken);

                    await EnqueueEmbeddingJobIfEligible(assetId, readyVersionId.Value, newRevision, cancellationToken);
                }
            }
        }

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

        if (deleted > 0)
        {
            Guid? readyVersionId = await dbContext.AssetVersions
                .AsNoTracking()
                .Where(v => v.AssetId == assetId && v.IsCurrent && v.ProcessingStatus == AssetVersionProcessingStatus.READY)
                .Select(v => (Guid?)v.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (readyVersionId.HasValue)
            {
                DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
                var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE assets
                    SET "SearchRevision" = "SearchRevision" + 1,
                        "UpdatedAt" = {now}
                    WHERE "Id" = {assetId} AND "DeletedAt" IS NULL
                    """, cancellationToken);

                if (affected > 0 && jobStore != null && embeddingOptions?.Value is { Enabled: true })
                {
                    var newRevision = await dbContext.Assets
                        .AsNoTracking()
                        .Where(a => a.Id == assetId)
                        .Select(a => a.SearchRevision)
                        .FirstOrDefaultAsync(cancellationToken);

                    await EnqueueEmbeddingJobIfEligible(assetId, readyVersionId.Value, newRevision, cancellationToken);
                }
            }
        }

        return deleted > 0;
    }

    public async Task<bool> Update(Guid id, string? title, string? description, decimal? price, Guid? categoryId, CancellationToken cancellationToken = default)
    {
        Asset? asset = await dbContext.Assets.FirstOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, cancellationToken);
        if (asset is null)
        {
            return false;
        }

        var titleChanged = title is not null && !string.Equals(asset.Title, title, StringComparison.Ordinal);
        var descriptionChanged = description is not null && !string.Equals(asset.Description, description, StringComparison.Ordinal);
        var categoryChanged = categoryId.HasValue && asset.CategoryId != categoryId.Value;
        var hasSearchableMetadataChange = titleChanged || descriptionChanged || categoryChanged;

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

        Guid? readyVersionId = null;
        if (hasSearchableMetadataChange)
        {
            readyVersionId = await dbContext.AssetVersions
                .AsNoTracking()
                .Where(v => v.AssetId == id && v.IsCurrent && v.ProcessingStatus == AssetVersionProcessingStatus.READY)
                .Select(v => (Guid?)v.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (readyVersionId.HasValue)
            {
                asset.SearchRevision += 1;
            }
        }

        asset.UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);

        if (hasSearchableMetadataChange && readyVersionId.HasValue)
        {
            await EnqueueEmbeddingJobIfEligible(id, readyVersionId.Value, asset.SearchRevision, cancellationToken);
        }

        return true;
    }

    private async Task EnqueueEmbeddingJobIfEligible(Guid assetId, Guid currentVersionId, long searchRevision, CancellationToken cancellationToken)
    {
        if (jobStore == null || embeddingOptions?.Value is not { Enabled: true } options)
        {
            return;
        }

        var assetMetadata = await dbContext.Assets
            .AsNoTracking()
            .Where(a => a.Id == assetId && a.DeletedAt == null)
            .Select(a => new
            {
                a.Title,
                a.Description,
                CategoryName = a.Category.Name
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (assetMetadata == null)
        {
            return;
        }

        List<string> tagNames = await dbContext.AssetTags
            .AsNoTracking()
            .Where(at => at.AssetId == assetId)
            .OrderBy(at => at.Tag.Name)
            .Select(at => at.Tag.Name)
            .ToListAsync(cancellationToken);

        var canonicalText = AssetPublicMetadataCanonicalizer.BuildCanonicalMetadata(
            assetMetadata.Title,
            assetMetadata.Description,
            assetMetadata.CategoryName,
            tagNames);

        var contentHash = AssetPublicMetadataCanonicalizer.ComputeContentHash(canonicalText);

        var payload = new EmbeddingGenerationPayload(
            assetId,
            currentVersionId,
            searchRevision,
            contentHash,
            EmbeddingModelKey.Compute(options),
            AssetPublicMetadataCanonicalizer.SCHEMA_VERSION);

        await jobStore.Enqueue(
            assetId,
            currentVersionId,
            AssetProcessingJobType.EMBEDDING_GENERATION,
            definitionVersion: 1,
            initialDelay: TimeSpan.Zero,
            payload,
            traceParent: null,
            cancellationToken);
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

    private IQueryable<Asset> PublicVisibleAssets()
    {
        return dbContext.Assets.AsNoTracking()
            .Where(a => a.DeletedAt == null
                && a.Versions.Any(v => v.IsCurrent && v.ProcessingStatus == AssetVersionProcessingStatus.READY));
    }

    private async Task<(SimilarRankedShortlist Ranked, HashSet<Guid> Signaled)> ApplyPopularityRanking(
        Guid sourceAssetId,
        IReadOnlyList<SimilarShortlistRow> shortlist,
        CancellationToken cancellationToken)
    {
        if (shortlist.Count == 0)
        {
            return (new SimilarRankedShortlist(shortlist, false), []);
        }

        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var windowStart = DateOnly.FromDateTime(
            now.AddDays(1 - SimilarAssetsConstants.POPULARITY_WINDOW_DAYS).UtcDateTime);
        var ids = shortlist.Select(r => r.Id).ToList();

        // Source-scoped recommendation engagement: clean impression/click aggregates only.
        var engagement = await dbContext.RecommendationDaily.AsNoTracking()
            .Where(d => d.SourceAssetId == sourceAssetId
                && d.DayUtc >= windowStart
                && ids.Contains(d.TargetAssetId))
            .GroupBy(d => d.TargetAssetId)
            .Select(g => new
            {
                TargetAssetId = g.Key,
                Clicks = g.Sum(x => x.ClickCount),
                Impressions = g.Sum(x => x.ImpressionCount)
            })
            .ToListAsync(cancellationToken);

        // Asset view counts are additive across days. Daily unique visitors are deliberately
        // not summed: daily distincts do not compose into period-unique counts.
        var views = await dbContext.ProductAnalyticsDaily.AsNoTracking()
            .Where(p => p.ProductType == AnalyticsProductKind.ASSET
                && p.DayUtc >= windowStart
                && ids.Contains(p.ProductId))
            .GroupBy(p => p.ProductId)
            .Select(g => new
            {
                ProductId = g.Key,
                ViewCount = g.Sum(x => x.Views)
            })
            .ToListAsync(cancellationToken);

        // Paid commerce as gross entitlement counts. No refund/dispute lifecycle exists
        // in source, so this is gross units sold, never net sales.
        var units = await dbContext.Purchases.AsNoTracking()
            .Where(p => ids.Contains(p.AssetId))
            .GroupBy(p => p.AssetId)
            .Select(g => new
            {
                AssetId = g.Key,
                GrossUnits = g.LongCount()
            })
            .ToListAsync(cancellationToken);

        var clicksById = engagement.ToDictionary(x => x.TargetAssetId, x => x.Clicks);
        var impressionsById = engagement.ToDictionary(x => x.TargetAssetId, x => x.Impressions);
        var viewsById = views.ToDictionary(x => x.ProductId, x => x.ViewCount);
        var unitsById = units.ToDictionary(x => x.AssetId, x => x.GrossUnits);
        var signaled = new HashSet<Guid>(
            shortlist
                .Where(r =>
                    GetSignalOrZero(clicksById, r.Id) > 0
                    || GetSignalOrZero(unitsById, r.Id) > 0
                    || GetSignalOrZero(impressionsById, r.Id) > 0
                    || GetSignalOrZero(viewsById, r.Id) > 0)
                .Select(r => r.Id));

        // Lexicographic popularity order, then Batch 1 metadata tie-breakers.
        // Candidates without any signal carry zeros and fall back to metadata order.
        var ranked = shortlist
            .OrderByDescending(r => GetSignalOrZero(clicksById, r.Id))
            .ThenByDescending(r => GetSignalOrZero(unitsById, r.Id))
            .ThenByDescending(r => GetSignalOrZero(impressionsById, r.Id))
            .ThenByDescending(r => GetSignalOrZero(viewsById, r.Id))
            .ThenByDescending(r => r.Jaccard)
            .ThenByDescending(r => r.RatingAverage)
            .ThenByDescending(r => r.RatingCount)
            .ThenBy(r => r.Id)
            .ToList();

        return (new SimilarRankedShortlist(ranked, false), signaled);
    }

    private async Task<(SimilarRankedShortlist Ranked, HashSet<Guid> PopularitySignaled)> RankPhaseA(
        Guid sourceAssetId,
        long sourceSearchRevision,
        IReadOnlyList<SimilarShortlistRow> shortlist,
        SimilarAssetsQueryOptions options,
        CancellationToken cancellationToken)
    {
        if (options.UsePopularityRanking)
        {
            return await ApplyPopularityRanking(sourceAssetId, shortlist, cancellationToken);
        }

        return (await TryRefineByCosine(
            sourceAssetId,
            sourceSearchRevision,
            shortlist,
            options,
            cancellationToken), []);
    }

    private async Task<(
        SimilarRankedShortlist Ranked,
        bool UsedPersonalization,
        HashSet<Guid> ClickTargets,
        HashSet<Guid> TagTargets,
        HashSet<Guid> PopularitySignaled)> ApplyPersonalRanking(
        Guid userId,
        Guid sourceAssetId,
        IReadOnlyList<SimilarShortlistRow> shortlist,
        Func<Task<(SimilarRankedShortlist Ranked, HashSet<Guid> PopularitySignaled)>> rankPhaseA,
        CancellationToken cancellationToken)
    {
        (SimilarRankedShortlist phaseA, HashSet<Guid> popularitySignaled) = await rankPhaseA();
        if (personalizationStore is null || shortlist.Count == 0)
        {
            return (phaseA, false, [], [], popularitySignaled);
        }

        var ids = shortlist.Select(r => r.Id).ToList();
        PersonalAffinitySignals? signals =
            await personalizationStore.GetSignals(userId, sourceAssetId, ids, cancellationToken);
        if (signals is null)
        {
            // Not opted in (or store unavailable): structural Phase A fallback.
            return (phaseA, false, [], [], popularitySignaled);
        }

        // Stable sort: full ties keep the Phase A order, so zero personal signals
        // reduce exactly to Phase A ordering.
        var reranked = phaseA.Rows
            .OrderByDescending(r => GetSignalOrZero(signals.ClicksByTargetId, r.Id))
            .ThenByDescending(r => GetSignalOrZero(signals.TagScoreByCandidateId, r.Id))
            .ToList();
        var clickTargets = new HashSet<Guid>(
            ids.Where(id => GetSignalOrZero(signals.ClicksByTargetId, id) > 0));
        var tagTargets = new HashSet<Guid>(
            ids.Where(id => GetSignalOrZero(signals.TagScoreByCandidateId, id) > 0));
        return (new SimilarRankedShortlist(reranked, phaseA.UsedSemanticRefinement), true, clickTargets, tagTargets, popularitySignaled);
    }

    private static long GetSignalOrZero(IReadOnlyDictionary<Guid, long> signals, Guid assetId)
    {
        return signals.GetValueOrDefault(assetId, 0);
    }

    private readonly record struct SimilarRankedShortlist(
        IReadOnlyList<SimilarShortlistRow> Rows,
        bool UsedSemanticRefinement);

    private async Task<SimilarRankedShortlist> TryRefineByCosine(
        Guid sourceAssetId,
        long sourceRevision,
        IReadOnlyList<SimilarShortlistRow> shortlist,
        SimilarAssetsQueryOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.AllowSemanticRefinement
            || string.IsNullOrWhiteSpace(options.CanonicalModelKey)
            || options.ExpectedDimension <= 0
            || string.IsNullOrWhiteSpace(options.ContentSchemaVersion)
            || shortlist.Count == 0)
        {
            return new SimilarRankedShortlist(shortlist, false);
        }

        var ids = new List<Guid>(shortlist.Count + 1) { sourceAssetId };
        ids.AddRange(shortlist.Select(row => row.Id));

        var embeddings = await dbContext.AssetEmbeddings.AsNoTracking()
            .Where(e => ids.Contains(e.AssetId)
                && e.ModelKey == options.CanonicalModelKey
                && e.Dimension == options.ExpectedDimension
                && e.ContentSchemaVersion == options.ContentSchemaVersion)
            .Select(e => new { e.AssetId, e.SourceRevision, e.Embedding })
            .ToListAsync(cancellationToken);

        var byAsset = embeddings
            .GroupBy(e => e.AssetId)
            .ToDictionary(g => g.Key, g => (g.First().SourceRevision, g.First().Embedding));

        if (!byAsset.TryGetValue(sourceAssetId, out (long SourceRevision, Vector Embedding) sourceEmbedding)
            || sourceEmbedding.SourceRevision != sourceRevision
            || !TryGetValidVector(sourceEmbedding.Embedding, options.ExpectedDimension, out var sourceVector))
        {
            return new SimilarRankedShortlist(shortlist, false);
        }

        var refined = new List<(SimilarShortlistRow Row, double Cosine)>(shortlist.Count);
        foreach (SimilarShortlistRow row in shortlist)
        {
            if (!byAsset.TryGetValue(row.Id, out (long SourceRevision, Vector Embedding) candidateEmbedding)
                || candidateEmbedding.SourceRevision != row.SearchRevision
                || !TryGetValidVector(candidateEmbedding.Embedding, options.ExpectedDimension, out var candidateVector))
            {
                return new SimilarRankedShortlist(shortlist, false);
            }

            refined.Add((row, CosineSimilarity(sourceVector, candidateVector)));
        }

        return new SimilarRankedShortlist(
            refined
                .OrderByDescending(x => x.Row.Jaccard)
                .ThenByDescending(x => x.Cosine)
                .ThenByDescending(x => x.Row.RatingAverage)
                .ThenByDescending(x => x.Row.RatingCount)
                .ThenBy(x => x.Row.Id)
                .Select(x => x.Row)
                .ToList(),
            true);
    }

    private static bool TryGetValidVector(Vector? embedding, int expectedDimension, out float[] vector)
    {
        vector = [];
        if (embedding is null)
        {
            return false;
        }

        var values = embedding.ToArray();
        if (values.Length != expectedDimension)
        {
            return false;
        }

        var sumSq = 0.0;
        foreach (var val in values)
        {
            if (float.IsNaN(val) || float.IsInfinity(val))
            {
                return false;
            }

            sumSq += (double)val * val;
        }

        if (sumSq < 1e-12)
        {
            return false;
        }

        vector = values;
        return true;
    }

    private static double CosineSimilarity(float[] left, float[] right)
    {
        var dot = 0.0;
        var leftNorm = 0.0;
        var rightNorm = 0.0;
        for (var i = 0; i < left.Length; i++)
        {
            var l = (double)left[i];
            var r = (double)right[i];
            dot += l * r;
            leftNorm += l * l;
            rightNorm += r * r;
        }

        return dot / Math.Sqrt(leftNorm * rightNorm);
    }

    private sealed record SimilarShortlistRow(
        Guid Id,
        double RatingAverage,
        int RatingCount,
        long SearchRevision,
        double Jaccard,
        int IntersectCount);

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", @"\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
