using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.Storage;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Outbox;

internal sealed class AssetBlobDeleteOutboxHandler(
    IAssetStorageService storageService,
    IAssetStore assetStore,
    ILogger<AssetBlobDeleteOutboxHandler> logger) : IOutboxMessageHandler
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string MessageType => OutboxMessageTypes.ASSET_BLOB_DELETE;

    public async Task Handle(OutboxMessage message, CancellationToken cancellationToken)
    {
        AssetBlobDeletePayload payload = JsonSerializer.Deserialize<AssetBlobDeletePayload>(message.Payload, _json)
            ?? throw new InvalidOperationException("Invalid AssetBlobDelete payload.");

        if (payload.AssetId == Guid.Empty
            || payload.AssetVersionId == Guid.Empty
            || string.IsNullOrWhiteSpace(payload.StorageKey)
            || !StorageVersionIdentity.IsImmutable(payload.StorageVersionId))
        {
            logger.LogError("Deferred blob deletion for asset {AssetId}: exact asset version or storage generation is missing", payload.AssetId);
            throw new InvalidOperationException("Asset blob deletion requires an exact provider object version ID.");
        }

        if (!await assetStore.CanDeleteRetiredUploadBlob(
                payload.AssetId,
                payload.AssetVersionId,
                payload.StorageKey,
                payload.StorageVersionId,
                cancellationToken).ConfigureAwait(false))
        {
            logger.LogWarning("Deferred blob deletion for asset {AssetId}, version {AssetVersionId}: latest relational protection or exact identity check failed",
                payload.AssetId, payload.AssetVersionId);
            throw new InvalidOperationException("Asset blob deletion is deferred while relational protection or exact identity checks fail.");
        }

        await storageService.Delete(payload.StorageKey, payload.StorageVersionId, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Deleted blob for asset {AssetId} via outbox", payload.AssetId);
    }
}
