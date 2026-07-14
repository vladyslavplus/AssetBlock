using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Outbox;

internal sealed class AssetIndexUpsertOutboxHandler(
    IAssetStore assetStore,
    IAssetSearchService searchService,
    ILogger<AssetIndexUpsertOutboxHandler> logger) : IOutboxMessageHandler
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string MessageType => OutboxMessageTypes.ASSET_INDEX_UPSERT;

    public async Task Handle(OutboxMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<AssetIndexUpsertPayload>(message.Payload, _json)
            ?? throw new InvalidOperationException("Invalid AssetIndexUpsert payload.");

        var asset = await assetStore.GetById(payload.AssetId, cancellationToken);
        if (asset is null || asset.DeletedAt.HasValue)
        {
            logger.LogInformation("Skipping index upsert for missing/delisted asset {AssetId}", payload.AssetId);
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
            AverageRating = 0
        };

        await searchService.IndexAsset(document, cancellationToken);
        logger.LogInformation("Indexed asset {AssetId} via outbox", asset.Id);
    }
}
