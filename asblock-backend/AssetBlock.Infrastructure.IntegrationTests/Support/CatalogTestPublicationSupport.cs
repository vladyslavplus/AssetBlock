using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.IntegrationTests.Support;

internal static class CatalogTestPublicationSupport
{
    public static async Task AttachTrustedPublicationForAllReadyAssets(
        ApplicationDbContext db,
        CancellationToken cancellationToken = default)
    {
        List<Asset> assets = await db.Assets
            .Where(a => a.DeletedAt == null)
            .ToListAsync(cancellationToken);

        foreach (Asset asset in assets)
        {
            List<AssetVersion> versions = await db.AssetVersions
                .Where(v => v.AssetId == asset.Id && v.ProcessingStatus == AssetVersionProcessingStatus.READY)
                .OrderBy(v => v.VersionNumber)
                .ToListAsync(cancellationToken);
            if (versions.Count == 0)
            {
                continue;
            }

            User author = await db.Users.FirstAsync(u => u.Id == asset.AuthorId, cancellationToken);
            Category category = await db.Categories.FirstAsync(c => c.Id == asset.CategoryId, cancellationToken);
            foreach (AssetVersion version in versions)
            {
                var alreadyApproved = await db.ModerationDecisionRecords.AsNoTracking()
                    .AnyAsync(
                        d => d.AssetId == asset.Id
                            && d.AssetVersionId == version.Id
                            && d.Outcome == ModerationSubmissionState.APPROVED,
                        cancellationToken);
                if (alreadyApproved)
                {
                    continue;
                }

                await ApprovedPublicationTestBuilder.AttachTrustedApprovedPublication(
                    db,
                    asset,
                    version,
                    author,
                    category,
                    asset.Title);
            }
        }
    }
}
