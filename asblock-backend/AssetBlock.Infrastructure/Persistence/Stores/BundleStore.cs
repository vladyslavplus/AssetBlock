using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Bundles;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class BundleStore(ApplicationDbContext dbContext, TimeProvider? timeProvider = null) : IBundleStore
{
    private const string LIKE_ESCAPE = "\\";

    public Task<Bundle?> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Bundles
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
    }

    public async Task<Bundle?> LockForUpdate(Guid id, CancellationToken cancellationToken = default)
    {
        Guid lockedId = await dbContext.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM bundles WHERE "Id" = {id} FOR UPDATE""")
            .FirstOrDefaultAsync(cancellationToken);

        if (lockedId == Guid.Empty)
        {
            return null;
        }

        return await dbContext.Bundles
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
    }

    public Task<BundleDetailDto?> GetPublicDetail(Guid id, CancellationToken cancellationToken = default)
    {
        return LoadDetail(id, sellerId: null, publicOnly: true, cancellationToken);
    }

    public Task<BundleDetailDto?> GetSellerDetail(Guid id, Guid sellerId, CancellationToken cancellationToken = default)
    {
        return LoadDetail(id, sellerId, publicOnly: false, cancellationToken);
    }

    private async Task<BundleDetailDto?> LoadDetail(
        Guid id,
        Guid? sellerId,
        bool publicOnly,
        CancellationToken cancellationToken)
    {
        IQueryable<Bundle> query = publicOnly
            ? AvailablePublicBundles().Where(b => b.Id == id)
            : dbContext.Bundles.AsNoTracking().Where(b => b.Id == id);
        if (sellerId is { } sid)
        {
            query = query.Where(b => b.SellerId == sid);
        }

        var row = await query
            .Select(b => new
            {
                b.Id,
                b.SellerId,
                SellerUsername = b.Seller.Username,
                b.CreatedAt,
                b.UpdatedAt,
                b.ArchivedAt,
                Revision = b.Revisions
                    .Where(r => r.IsCurrent)
                    .Select(r => new
                    {
                        r.Id,
                        r.RevisionNumber,
                        r.Title,
                        r.Description,
                        r.Price,
                        r.ListPriceTotal,
                        r.Currency,
                        Items = r.Items
                            .OrderBy(i => i.Position)
                            .Select(i => new
                            {
                                i.AssetId,
                                i.AssetTitleSnapshot,
                                i.ListPriceSnapshot,
                                i.Position,
                                AssetAuthorId = i.Asset == null ? (Guid?)null : i.Asset.AuthorId,
                                AssetDeletedAt = i.Asset == null ? null : i.Asset.DeletedAt,
                                CurrentVersionNumber = i.Asset == null
                                    ? null
                                    : i.Asset.Versions.Where(v => v.IsCurrent).Select(v => (int?)v.VersionNumber).FirstOrDefault(),
                                LicenseCode = i.Asset == null
                                    ? null
                                    : i.Asset.Versions.Where(v => v.IsCurrent).Select(v => (AssetLicenseCode?)v.LicenseCode).FirstOrDefault(),
                                LicenseDisplayName = i.Asset == null
                                    ? null
                                    : i.Asset.Versions.Where(v => v.IsCurrent).Select(v => v.LicenseDisplayName).FirstOrDefault()
                            })
                            .ToList()
                    })
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row?.Revision is null)
        {
            return null;
        }

        var revision = row.Revision;
        var items = revision.Items.Select(i =>
        {
            string? reason = null;
            var available = false;
            if (i.AssetId is null)
            {
                reason = "Asset deleted";
            }
            else if (i.AssetAuthorId is null)
            {
                reason = "Asset missing";
            }
            else if (i.AssetDeletedAt is not null)
            {
                reason = "Asset is delisted";
            }
            else if (i.AssetAuthorId != row.SellerId)
            {
                reason = "Asset owner mismatch";
            }
            else if (i.CurrentVersionNumber is null)
            {
                reason = "Current version missing";
            }
            else
            {
                available = true;
            }

            return new BundleItemDto(
                i.AssetId,
                i.AssetTitleSnapshot,
                i.ListPriceSnapshot,
                i.Position,
                available,
                reason,
                i.CurrentVersionNumber,
                i.LicenseCode?.ToString(),
                i.LicenseDisplayName);
        }).ToList();

        var isArchived = row.ArchivedAt is not null;
        var isAvailable = !isArchived
            && items.Count > 0
            && items.All(i => i.IsAvailable);
        var savings = revision.ListPriceTotal - revision.Price;
        var savingsPercent = revision.ListPriceTotal == 0
            ? 0
            : Math.Round(savings / revision.ListPriceTotal * 100m, 2);

        return new BundleDetailDto(
            row.Id,
            revision.Id,
            revision.RevisionNumber,
            revision.Title,
            revision.Description,
            revision.Price,
            revision.ListPriceTotal,
            savings,
            savingsPercent,
            revision.Currency,
            row.SellerId,
            row.SellerUsername,
            row.CreatedAt,
            row.UpdatedAt,
            row.ArchivedAt,
            isArchived,
            isAvailable,
            items);
    }

    public async Task<PagedResult<BundleListItemDto>> ListPublic(
        ListBundlesRequest request,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Bundle> query = AvailablePublicBundles();

        if (request.SellerId is { } sellerId)
        {
            query = query.Where(b => b.SellerId == sellerId);
        }

        if (request.MinPrice is { } minPrice)
        {
            query = query.Where(b => b.Revisions.Any(r => r.IsCurrent && r.Price >= minPrice));
        }

        if (request.MaxPrice is { } maxPrice)
        {
            query = query.Where(b => b.Revisions.Any(r => r.IsCurrent && r.Price <= maxPrice));
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var searchText = request.Search.Trim();
            var likePattern = $"%{EscapeLikePattern(searchText)}%";
            query = query.Where(b =>
                b.Revisions.Any(r => r.IsCurrent
                    && (EF.Functions.ILike(r.Title, likePattern, LIKE_ESCAPE)
                        || (r.Description != null && EF.Functions.ILike(r.Description, likePattern, LIKE_ESCAPE)))));
        }

        var total = await query.CountAsync(cancellationToken);
        IQueryable<BundleListItemDto> projected = ProjectList(query, request.SortBy, request.SortDirection, ListBundlesRequest.AllowedSortBy, "CreatedAt");

        var page = Math.Max(PagedRequest.DEFAULT_PAGE, request.Page);
        var pageSize = Math.Clamp(request.PageSize, PagedRequest.MIN_PAGE_SIZE, PagedRequest.MAX_PAGE_SIZE);
        List<BundleListItemDto> items = await projected.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<BundleListItemDto>(items, total, page, pageSize);
    }

    public async Task<PagedResult<BundleListItemDto>> ListForSeller(
        Guid sellerId,
        ListMyBundlesRequest request,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Bundle> query = dbContext.Bundles.AsNoTracking().Where(b => b.SellerId == sellerId);

        if (request.ArchivedOnly == true)
        {
            query = query.Where(b => b.ArchivedAt != null);
        }
        else if (request.ArchivedOnly == false)
        {
            query = query.Where(b => b.ArchivedAt == null);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var searchText = request.Search.Trim();
            var likePattern = $"%{EscapeLikePattern(searchText)}%";
            query = query.Where(b =>
                b.Revisions.Any(r => r.IsCurrent
                    && (EF.Functions.ILike(r.Title, likePattern, LIKE_ESCAPE)
                        || (r.Description != null && EF.Functions.ILike(r.Description, likePattern, LIKE_ESCAPE)))));
        }

        var total = await query.CountAsync(cancellationToken);
        IQueryable<BundleListItemDto> projected = ProjectList(query, request.SortBy, request.SortDirection, ListMyBundlesRequest.AllowedSortBy, "UpdatedAt");

        var page = Math.Max(PagedRequest.DEFAULT_PAGE, request.Page);
        var pageSize = Math.Clamp(request.PageSize, PagedRequest.MIN_PAGE_SIZE, PagedRequest.MAX_PAGE_SIZE);
        List<BundleListItemDto> items = await projected.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<BundleListItemDto>(items, total, page, pageSize);
    }

    public async Task<(Bundle Bundle, BundleRevision Revision)> CreateWithRevision(
        Guid sellerId,
        string title,
        string? description,
        decimal price,
        string currency,
        decimal listPriceTotal,
        IReadOnlyList<BundleRevisionItemDraft> items,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var bundle = new Bundle
        {
            Id = Guid.NewGuid(),
            SellerId = sellerId,
            CreatedAt = now
        };
        var revision = new BundleRevision
        {
            Id = Guid.NewGuid(),
            BundleId = bundle.Id,
            RevisionNumber = 1,
            IsCurrent = true,
            Title = title,
            Description = description,
            Price = price,
            Currency = currency,
            ListPriceTotal = listPriceTotal,
            CreatedAt = now
        };

        dbContext.Bundles.Add(bundle);
        dbContext.BundleRevisions.Add(revision);
        foreach (BundleRevisionItemDraft item in items)
        {
            dbContext.BundleRevisionItems.Add(new BundleRevisionItem
            {
                Id = Guid.NewGuid(),
                BundleRevisionId = revision.Id,
                AssetId = item.AssetId,
                Position = item.Position,
                AssetTitleSnapshot = item.AssetTitleSnapshot,
                ListPriceSnapshot = item.ListPriceSnapshot
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return (bundle, revision);
    }

    public async Task<BundleRevision> PublishNextRevision(
        Guid bundleId,
        string title,
        string? description,
        decimal price,
        string currency,
        decimal listPriceTotal,
        IReadOnlyList<BundleRevisionItemDraft> items,
        CancellationToken cancellationToken = default)
    {
        Bundle locked = await LockForUpdate(bundleId, cancellationToken)
            ?? throw new InvalidOperationException($"Bundle {bundleId} was not found for revision publish.");

        var maxRevision = await dbContext.BundleRevisions
            .Where(r => r.BundleId == bundleId)
            .MaxAsync(r => (int?)r.RevisionNumber, cancellationToken) ?? 0;

        await dbContext.BundleRevisions
            .Where(r => r.BundleId == bundleId && r.IsCurrent)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.IsCurrent, false), cancellationToken);

        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var revision = new BundleRevision
        {
            Id = Guid.NewGuid(),
            BundleId = locked.Id,
            RevisionNumber = maxRevision + 1,
            IsCurrent = true,
            Title = title,
            Description = description,
            Price = price,
            Currency = currency,
            ListPriceTotal = listPriceTotal,
            CreatedAt = now
        };

        dbContext.BundleRevisions.Add(revision);
        foreach (BundleRevisionItemDraft item in items)
        {
            dbContext.BundleRevisionItems.Add(new BundleRevisionItem
            {
                Id = Guid.NewGuid(),
                BundleRevisionId = revision.Id,
                AssetId = item.AssetId,
                Position = item.Position,
                AssetTitleSnapshot = item.AssetTitleSnapshot,
                ListPriceSnapshot = item.ListPriceSnapshot
            });
        }

        await dbContext.Bundles
            .Where(b => b.Id == bundleId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(b => b.UpdatedAt, now), cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        return revision;
    }

    public async Task<bool> TryArchive(Guid id, Guid sellerId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var updated = await dbContext.Bundles
            .Where(b => b.Id == id && b.SellerId == sellerId && b.ArchivedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(b => b.ArchivedAt, now)
                    .SetProperty(b => b.UpdatedAt, now),
                cancellationToken);
        return updated == 1;
    }

    public async Task<BundleCheckoutSnapshot?> GetCheckoutSnapshot(
        Guid bundleId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await dbContext.Bundles
            .AsNoTracking()
            .Where(b => b.Id == bundleId && b.ArchivedAt == null)
            .Select(b => new
            {
                b.Id,
                b.SellerId,
                CurrentRevision = b.Revisions
                    .Where(r => r.IsCurrent)
                    .Select(r => new
                    {
                        r.Id,
                        r.Title,
                        r.Price,
                        r.Currency,
                        r.ListPriceTotal,
                        Items = r.Items
                            .OrderBy(i => i.Position)
                            .Select(i => new
                            {
                                i.AssetId,
                                i.Position,
                                i.AssetTitleSnapshot,
                                i.ListPriceSnapshot,
                                Asset = i.Asset == null ? null : new
                                {
                                    i.Asset.AuthorId,
                                    i.Asset.DeletedAt,
                                    CurrentVersion = i.Asset.Versions
                                        .Where(v => v.IsCurrent)
                                        .Select(v => new
                                        {
                                            v.Id,
                                            v.VersionNumber,
                                            v.LicenseCode,
                                            v.LicenseTemplateVersion,
                                            v.LicenseDisplayName,
                                            v.LicenseTerms
                                        })
                                        .FirstOrDefault()
                                }
                            })
                            .ToList()
                    })
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (snapshot?.CurrentRevision is null)
        {
            return null;
        }

        var revision = snapshot.CurrentRevision;
        var items = revision.Items;

        if (items.Count == 0
            || items.Any(i =>
                i.AssetId is null
                || i.Asset is null
                || i.Asset.DeletedAt != null
                || i.Asset.AuthorId != snapshot.SellerId
                || i.Asset.CurrentVersion is null))
        {
            return null;
        }

        return new BundleCheckoutSnapshot(
            snapshot.Id,
            revision.Id,
            snapshot.SellerId,
            revision.Title,
            revision.Price,
            revision.Currency,
            revision.ListPriceTotal,
            items.Select(i => new BundleCheckoutItemSnapshot(
                i.AssetId!.Value,
                i.Asset!.CurrentVersion!.Id,
                i.Position,
                i.AssetTitleSnapshot,
                i.ListPriceSnapshot,
                i.Asset.CurrentVersion.VersionNumber,
                i.Asset.CurrentVersion.LicenseCode,
                i.Asset.CurrentVersion.LicenseTemplateVersion,
                i.Asset.CurrentVersion.LicenseDisplayName,
                i.Asset.CurrentVersion.LicenseTerms)).ToList());
    }

    public async Task<bool> TryRestore(Guid id, Guid sellerId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        BundleCheckoutSnapshot? snapshot = await GetCheckoutSnapshot(id, cancellationToken);
        if (snapshot is null)
        {
            // Still allow restore only when current revision assets are valid; archived bundles
            // are excluded from GetCheckoutSnapshot, so validate manually.
            var available = await IsCurrentRevisionAvailable(id, cancellationToken);
            if (!available)
            {
                return false;
            }
        }
        else if (snapshot.SellerId != sellerId)
        {
            return false;
        }

        var updated = await dbContext.Bundles
            .Where(b => b.Id == id && b.SellerId == sellerId && b.ArchivedAt != null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(b => b.ArchivedAt, (DateTimeOffset?)null)
                    .SetProperty(b => b.UpdatedAt, now),
                cancellationToken);
        return updated == 1;
    }

    public async Task LockAssetsInOrder(IReadOnlyList<Guid> assetIds, CancellationToken cancellationToken = default)
    {
        Guid[] distinctIds = assetIds.Distinct().OrderBy(id => id).ToArray();
        if (distinctIds.Length == 0)
        {
            return;
        }

        _ = await dbContext.Assets
            .FromSqlRaw("""SELECT * FROM assets WHERE "Id" = ANY({0}) ORDER BY "Id" FOR UPDATE""", distinctIds)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public Task<Guid?> GetPublicAnalyticsSellerId(Guid bundleId, CancellationToken cancellationToken = default)
    {
        return AvailablePublicBundles()
            .Where(b => b.Id == bundleId)
            .Select(b => (Guid?)b.SellerId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private IQueryable<Bundle> AvailablePublicBundles()
    {
        return dbContext.Bundles
            .AsNoTracking()
            .Where(b => b.ArchivedAt == null)
            .Where(b => b.Revisions.Any(r => r.IsCurrent
                && r.Items.Any()
                && !r.Items.Any(i =>
                    i.AssetId == null
                    || i.Asset == null
                    || i.Asset.DeletedAt != null
                    || i.Asset.AuthorId != b.SellerId
                    || !i.Asset.Versions.Any(v => v.IsCurrent))));
    }

    private async Task<bool> IsCurrentRevisionAvailable(Guid bundleId, CancellationToken cancellationToken)
    {
        return await dbContext.Bundles
            .AsNoTracking()
            .Where(b => b.Id == bundleId)
            .Where(b => b.Revisions.Any(r => r.IsCurrent
                && r.Items.Any()
                && !r.Items.Any(i =>
                    i.AssetId == null
                    || i.Asset == null
                    || i.Asset.DeletedAt != null
                    || i.Asset.AuthorId != b.SellerId
                    || !i.Asset.Versions.Any(v => v.IsCurrent))))
            .AnyAsync(cancellationToken);
    }

    private static IQueryable<BundleListItemDto> ProjectList(
        IQueryable<Bundle> query,
        string? sortBy,
        SortDirection sortDirection,
        IReadOnlySet<string> allowed,
        string defaultSort)
    {
        var key = string.IsNullOrWhiteSpace(sortBy) || !allowed.Contains(sortBy)
            ? defaultSort
            : sortBy.Trim();
        var isDesc = sortDirection == SortDirection.DESC;

        // Sort on entity columns before projection so UpdatedAt is available.
        query = key.ToUpperInvariant() switch
        {
            "TITLE" => isDesc
                ? query.OrderByDescending(b => b.Revisions.Where(r => r.IsCurrent).Select(r => r.Title).FirstOrDefault())
                    .ThenBy(b => b.Id)
                : query.OrderBy(b => b.Revisions.Where(r => r.IsCurrent).Select(r => r.Title).FirstOrDefault())
                    .ThenBy(b => b.Id),
            "PRICE" => isDesc
                ? query.OrderByDescending(b => b.Revisions.Where(r => r.IsCurrent).Select(r => r.Price).FirstOrDefault())
                    .ThenBy(b => b.Id)
                : query.OrderBy(b => b.Revisions.Where(r => r.IsCurrent).Select(r => r.Price).FirstOrDefault())
                    .ThenBy(b => b.Id),
            "UPDATEDAT" => isDesc
                ? query.OrderByDescending(b => b.UpdatedAt ?? b.CreatedAt).ThenBy(b => b.Id)
                : query.OrderBy(b => b.UpdatedAt ?? b.CreatedAt).ThenBy(b => b.Id),
            _ => isDesc
                ? query.OrderByDescending(b => b.CreatedAt).ThenBy(b => b.Id)
                : query.OrderBy(b => b.CreatedAt).ThenBy(b => b.Id)
        };

        return query.Select(b => new BundleListItemDto(
            b.Id,
            b.Revisions.Where(r => r.IsCurrent).Select(r => r.Id).FirstOrDefault(),
            b.Revisions.Where(r => r.IsCurrent).Select(r => r.RevisionNumber).FirstOrDefault(),
            b.Revisions.Where(r => r.IsCurrent).Select(r => r.Title).FirstOrDefault() ?? string.Empty,
            b.Revisions.Where(r => r.IsCurrent).Select(r => r.Description).FirstOrDefault(),
            b.Revisions.Where(r => r.IsCurrent).Select(r => r.Price).FirstOrDefault(),
            b.Revisions.Where(r => r.IsCurrent).Select(r => r.ListPriceTotal).FirstOrDefault(),
            b.Revisions.Where(r => r.IsCurrent).Select(r => r.ListPriceTotal - r.Price).FirstOrDefault(),
            b.Revisions.Where(r => r.IsCurrent).Select(r =>
                r.ListPriceTotal == 0
                    ? 0
                    : Math.Round((r.ListPriceTotal - r.Price) / r.ListPriceTotal * 100m, 2)).FirstOrDefault(),
            b.Revisions.Where(r => r.IsCurrent).Select(r => r.Currency).FirstOrDefault() ?? "usd",
            b.Revisions.Where(r => r.IsCurrent).SelectMany(r => r.Items).Count(),
            b.SellerId,
            b.Seller.Username,
            b.CreatedAt,
            b.ArchivedAt != null,
            b.ArchivedAt == null
            && b.Revisions.Any(r => r.IsCurrent
                && r.Items.Any()
                && !r.Items.Any(i =>
                    i.AssetId == null
                    || i.Asset == null
                    || i.Asset.DeletedAt != null
                    || i.Asset.AuthorId != b.SellerId
                    || !i.Asset.Versions.Any(v => v.IsCurrent)))));
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", @"\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
