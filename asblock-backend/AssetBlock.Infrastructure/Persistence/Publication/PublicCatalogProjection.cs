using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Publication;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Publication;

internal static class PublicCatalogProjection
{
    internal sealed record Row(
        Guid Id,
        string ApprovedMetadataJson,
        decimal Price,
        Guid AuthorId,
        string AuthorUsername,
        DateTimeOffset CreatedAt,
        double RatingAverage);

    internal static IQueryable<Row> SelectRows(IQueryable<Asset> assets) =>
        assets.Select(a => new Row(
            a.Id,
            a.CurrentPublicationSnapshot!.ApprovedMetadataJson,
            a.Price,
            a.AuthorId,
            a.Author.Username,
            a.CreatedAt,
            a.RatingAverage));

    internal static async Task<List<AssetListItem>> MapRows(
        ApplicationDbContext db,
        IReadOnlyList<Row> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var parsed = new List<(Row Row, ApprovedPublicationMetadata.PublicProjection Projection)>(rows.Count);
        foreach (Row row in rows)
        {
            if (!ApprovedPublicationMetadata.TryReadPublicProjection(
                row.ApprovedMetadataJson,
                out ApprovedPublicationMetadata.PublicProjection projection))
            {
                continue;
            }

            parsed.Add((row, projection));
        }

        if (parsed.Count == 0)
        {
            return [];
        }

        var categoryIds = parsed.Select(p => p.Projection.CategoryId).ToHashSet();
        Dictionary<Guid, string> categoryNames = await db.Categories
            .AsNoTracking()
            .Where(c => categoryIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        var items = new List<AssetListItem>(parsed.Count);
        foreach ((Row row, ApprovedPublicationMetadata.PublicProjection projection) in parsed)
        {
            if (!categoryNames.TryGetValue(projection.CategoryId, out var categoryName))
            {
                continue;
            }

            items.Add(new AssetListItem(
                row.Id,
                projection.Title,
                projection.Description,
                row.Price,
                projection.CategoryId,
                categoryName,
                row.AuthorId,
                row.AuthorUsername,
                row.CreatedAt,
                projection.Tags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList(),
                row.RatingAverage));
        }

        return items;
    }
}
