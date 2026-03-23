using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Assets;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.Events;

internal sealed class AssetIndexEventHandler(
    IAssetStore assetStore,
    IAssetSearchService searchService,
    ILogger<AssetIndexEventHandler> logger) : INotificationHandler<AssetCreatedEvent>
{
    public async Task Handle(AssetCreatedEvent notification, CancellationToken cancellationToken)
    {
        var asset = await assetStore.GetById(notification.AssetId, cancellationToken);
        if (asset == null)
        {
            logger.LogWarning("AssetIndexEventHandler: Asset {AssetId} not found", notification.AssetId);
            return;
        }

        var tags = asset.AssetTags
            .Select(at => at.Tag.Name.Trim().ToLowerInvariant())
            .Where(n => n.Length > 0)
            .ToList();

        var document = new AssetDocument
        {
            Id = asset.Id,
            Title = asset.Title,
            Description = asset.Description,
            Price = asset.Price,
            CategoryId = asset.CategoryId,
            CategoryName = asset.Category.Name,
            CategorySlug = asset.Category.Slug,
            AuthorId = asset.AuthorId,
            AuthorUsername = asset.Author.Username,
            StorageKey = asset.StorageKey,
            Tags = tags,
            CreatedAt = asset.CreatedAt,
            AverageRating = 0 // On creation it is 0
        };

        try
        {
            await searchService.IndexAsset(document, cancellationToken);
            logger.LogInformation("Indexed asset {AssetId} in Elasticsearch", asset.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to index asset {AssetId}", asset.Id);
        }
    }
}
